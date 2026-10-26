using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;

namespace RaidZero.Backend.Raids;

/// <summary>A status code and JSON body, kept as a string so idempotency can store and replay it exactly.</summary>
public sealed record ServerResponse(int StatusCode, string Body)
{
    public static ServerResponse Json(int statusCode, object body) => new(statusCode, JsonSerializer.Serialize(body, JsonDefaults.Options));

    public static ServerResponse Error(int statusCode, string error, string message, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["error"] = error, ["message"] = message };
        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra, JsonDefaults.Options).EnumerateObject())
            {
                body[property.Name] = property.Value;
            }
        }

        return new(statusCode, JsonSerializer.Serialize(body, JsonDefaults.Options));
    }

    public IResult ToResult() => Results.Content(Body, "application/json", Encoding.UTF8, StatusCode);
}

/// <summary>
/// Thrown partway through a write when the batch must be rejected after some rows already changed.
/// The transaction rolls back and the caller returns <see cref="Response"/>; nothing is stored for idempotency.
/// </summary>
public sealed class RaidRejection(ServerResponse response) : Exception(response.Body)
{
    public ServerResponse Response { get; } = response;
}

/// <summary>
/// Makes game server calls safe to retry. The response to a key is stored in the same transaction as the
/// change it describes, so a retry after a timeout replays the original answer instead of applying twice.
/// Keys are scoped to the calling server. Reusing a key for a different request is a 409, because it
/// means a bug in the server's outbox that would otherwise silently drop a report.
/// </summary>
public static class Idempotency
{
    public static string HashRequest(string method, string path, string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{method} {path}\n{body}")));

    public static async Task<ServerResponse> RunAsync(
        Database db, string serverId, string key, string requestHash, TimeProvider time,
        Func<SqliteConnection, SqliteTransaction, Task<ServerResponse>> work, CancellationToken ct)
    {
        var scopedKey = $"{serverId}:{key}";
        try
        {
            return await db.WriteAsync(async (c, tx) =>
            {
                var stored = (await c.QueryAsync<StoredKey>(
                    "SELECT request_hash AS RequestHash, response AS Response FROM idempotency_keys WHERE key = @scopedKey",
                    new { scopedKey }, transaction: tx)).FirstOrDefault();
                if (stored is not null)
                {
                    if (stored.RequestHash != requestHash)
                    {
                        return ServerResponse.Error(409, "idempotency_key_reused", "This Idempotency-Key was already used for a different request.");
                    }

                    var replay = JsonSerializer.Deserialize<StoredResponse>(stored.Response, JsonDefaults.Options)!;
                    return new ServerResponse(replay.Status, replay.Body);
                }

                var response = await work(c, tx);
                await c.ExecuteAsync(
                    "INSERT INTO idempotency_keys (key, request_hash, response, created_at) VALUES (@scopedKey, @requestHash, @response, @now)",
                    new
                    {
                        scopedKey,
                        requestHash,
                        response = JsonSerializer.Serialize(new StoredResponse(response.StatusCode, response.Body), JsonDefaults.Options),
                        now = Timestamps.Format(time.GetUtcNow()),
                    },
                    transaction: tx);
                return response;
            }, ct);
        }
        catch (RaidRejection rejection)
        {
            return rejection.Response;
        }
    }

    private sealed record StoredResponse(int Status, string Body);

    private sealed class StoredKey
    {
        public string RequestHash { get; set; } = "";

        public string Response { get; set; } = "";
    }
}
