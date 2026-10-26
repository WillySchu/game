using System.Security.Claims;
using System.Text.Json;
using RaidZero.Backend.Auth;
using RaidZero.Shared.Catalog;

namespace RaidZero.Backend.Raids;

public static class RaidEndpoints
{
    public const string CatalogVersionHeader = "X-Catalog-Version";
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public sealed record TicketRequest(string? MapId);

    public static void MapRaidEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/v1/me").RequireAuthorization(Policies.Player);
        me.MapPost("/raid-tickets", async (TicketRequest body, ClaimsPrincipal user, RaidService raids, CancellationToken ct) =>
            (await raids.CreateTicketAsync(user.GetPlayerId(), body.MapId, ct)).ToResult());
        me.MapGet("/raids/{raidId}", async (string raidId, ClaimsPrincipal user, RaidService raids, CancellationToken ct) =>
            (await raids.GetResultAsync(user.GetPlayerId(), raidId, ct)).ToResult());

        var server = app.MapGroup("/v1/server").RequireAuthorization(Policies.GameServer).AddEndpointFilter(RequireMatchingCatalogAsync);
        server.MapPost("/raids/{raidId}/join", (string raidId, HttpRequest request, ClaimsPrincipal user, RaidService raids, CancellationToken ct) =>
            ServerCallAsync<RaidService.JoinRequest>(request, ct, (key, hash, body) => raids.JoinAsync(user.GetServerId(), raidId, key, hash, body, ct)));
        server.MapPost("/raids/{raidId}/heartbeat", async (string raidId, ClaimsPrincipal user, RaidService raids, CancellationToken ct) =>
            (await raids.HeartbeatAsync(user.GetServerId(), raidId, ct)).ToResult());
        server.MapPost("/raids/{raidId}/outcomes", (string raidId, HttpRequest request, ClaimsPrincipal user, RaidService raids, CancellationToken ct) =>
            ServerCallAsync<RaidService.OutcomeRequest>(request, ct, (key, hash, body) => raids.ReportOutcomeAsync(user.GetServerId(), raidId, key, hash, body, ct)));
        server.MapPost("/raids/{raidId}/close", (string raidId, HttpRequest request, ClaimsPrincipal user, RaidService raids, CancellationToken ct) =>
            ServerCallAsync<RaidService.CloseRequest>(request, ct, (key, hash, _) => raids.CloseAsync(user.GetServerId(), raidId, key, hash, ct)));
    }

    /// <summary>A server running an older build mustn't commit items under rules the backend no longer uses.</summary>
    private static async ValueTask<object?> RequireMatchingCatalogAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<ItemCatalog>().Version;
        var sent = context.HttpContext.Request.Headers[CatalogVersionHeader].ToString();
        if (sent != expected)
        {
            return ServerResponse.Error(409, "catalog_mismatch",
                $"This backend runs item catalog {expected}; the server sent '{sent}'. Rebuild the server with the current catalog.",
                new { Expected = expected }).ToResult();
        }

        return await next(context);
    }

    /// <summary>
    /// Reads the raw body (the idempotency hash covers it exactly as sent), requires an Idempotency-Key, and
    /// hands both to the service.
    /// </summary>
    private static async Task<IResult> ServerCallAsync<T>(HttpRequest request, CancellationToken ct, Func<string, string, T, Task<ServerResponse>> call)
        where T : class
    {
        var key = request.Headers[IdempotencyKeyHeader].ToString();
        if (key.Length is 0 or > 200)
        {
            return ServerResponse.Error(422, "missing_idempotency_key", $"Send an {IdempotencyKeyHeader} header (1 to 200 characters).").ToResult();
        }

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(body) ? "{}" : body, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            return ServerResponse.Error(422, "invalid_request", $"The body isn't valid JSON: {ex.Message}").ToResult();
        }

        if (parsed is null)
        {
            return ServerResponse.Error(422, "invalid_request", "A JSON body is required.").ToResult();
        }

        return (await call(key, Idempotency.HashRequest(request.Method, request.Path, body), parsed)).ToResult();
    }
}
