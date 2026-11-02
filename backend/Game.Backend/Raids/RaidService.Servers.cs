using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;

namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    private const int MaxReportFieldLength = 64;

    /// <summary>
    /// POST /v1/server/ready: a game server waiting for a raid says what it has loaded.
    /// </summary>
    /// <param name="MapId">The map the server has loaded. Tickets for other maps don't open raids on it.</param>
    /// <param name="Build">What the server was built from (CI puts the commit here), for the admin view and logs.</param>
    /// <param name="NetworkCode">Its networked code's fingerprint. A ticket request that sends one only goes to servers with the same.</param>
    public sealed record ReadyRequest(string? MapId, string? Build = null, string? NetworkCode = null);

    public sealed record ReadyResponse(bool Ok, int ReportEverySeconds);

    /// <summary>What a configured server last reported, and the raid it's running, for the admin view.</summary>
    public sealed record ServerStatus(string Id, string Host, int Port, string? MapId, string? Build, string? NetworkCode, string? ReportedAt, bool Ready, string? RaidId, string? RaidState);

    /// <summary>
    /// Records that the server is up and waiting, with what it has loaded. Servers send this every few seconds
    /// until their raid starts (their first player joins), so a server that stops reporting stops getting
    /// raids. The catalog version comes from the X-Catalog-Version header, which the endpoint has already checked.
    /// </summary>
    public Task<ServerResponse> ReportReadyAsync(string serverId, ReadyRequest request, string catalogVersion, CancellationToken ct = default)
    {
        if (!IsValidMapId(request.MapId))
        {
            return Task.FromResult(ServerResponse.Error(422, "invalid_request", "map_id must be lowercase letters, digits and underscores."));
        }

        if (!IsValidReportField(request.Build) || !IsValidReportField(request.NetworkCode))
        {
            return Task.FromResult(ServerResponse.Error(422, "invalid_request", $"build and network_code must be printable and at most {MaxReportFieldLength} characters."));
        }

        return _db.WriteAsync(async (c, tx) =>
        {
            await c.ExecuteAsync(
                """
                INSERT INTO game_servers (id, map_id, build, network_code, catalog_version, reported_at)
                VALUES (@serverId, @mapId, @build, @networkCode, @catalogVersion, @now)
                ON CONFLICT (id) DO UPDATE SET
                    map_id = excluded.map_id, build = excluded.build, network_code = excluded.network_code,
                    catalog_version = excluded.catalog_version, reported_at = excluded.reported_at
                """,
                new { serverId, mapId = request.MapId, build = request.Build, networkCode = request.NetworkCode, catalogVersion, now = Now() },
                transaction: tx);
            return ServerResponse.Json(200, new ReadyResponse(true, Math.Max(1, _options.ReadyTtlSeconds / 3)));
        }, ct);
    }

    /// <summary>Every configured server with its last report and current raid, in configuration order.</summary>
    public async Task<IReadOnlyList<ServerStatus>> ListServersAsync(CancellationToken ct = default)
    {
        var reports = await _db.ReadAsync(c => LoadReportsAsync(c, null), ct);
        var raids = (await _db.ReadAsync(c => c.QueryAsync<RaidRow>(
            """
            SELECT id AS Id, map_id AS MapId, server_id AS ServerId, state AS State, lease_expires_at AS LeaseExpiresAt
            FROM raids WHERE state IN ('pending', 'active')
            """), ct)).ToList();

        var freshAfter = FreshAfter();
        return _servers.All.Select(server =>
        {
            reports.TryGetValue(server.Id, out var report);
            var raid = raids.FirstOrDefault(r => r.ServerId == server.Id);
            return new ServerStatus(server.Id, server.Host, server.Port, report?.MapId, report?.Build, report?.NetworkCode, report?.ReportedAt,
                IsFresh(report, freshAfter), raid?.Id, raid?.State);
        }).ToList();
    }

    /// <summary>
    /// The server's process exits once its raid closes or is aborted, so its report no longer describes a
    /// running server: it gets raids again when the fresh process reports.
    /// </summary>
    private static Task ForgetReadyAsync(SqliteConnection c, SqliteTransaction tx, string raidId) =>
        c.ExecuteAsync(
            "UPDATE game_servers SET reported_at = NULL WHERE id = (SELECT server_id FROM raids WHERE id = @raidId)",
            new { raidId }, transaction: tx);

    private static async Task<Dictionary<string, ServerReportRow>> LoadReportsAsync(SqliteConnection c, SqliteTransaction? tx) =>
        (await c.QueryAsync<ServerReportRow>(
            """
            SELECT id AS Id, map_id AS MapId, build AS Build, network_code AS NetworkCode, catalog_version AS CatalogVersion, reported_at AS ReportedAt
            FROM game_servers
            """,
            transaction: tx)).ToDictionary(r => r.Id, StringComparer.Ordinal);

    private string FreshAfter() => Timestamps.Format(_time.GetUtcNow().AddSeconds(-_options.ReadyTtlSeconds));

    private static bool IsFresh(ServerReportRow? report, string freshAfter) =>
        report?.ReportedAt is not null && string.CompareOrdinal(report.ReportedAt, freshAfter) >= 0;

    private static bool IsValidMapId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? mapId) =>
        !string.IsNullOrEmpty(mapId) && mapId.Length <= 64 && mapId.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_');

    private static bool IsValidReportField(string? value) =>
        value is null || (value.Length <= MaxReportFieldLength && value.All(ch => ch >= ' ' && ch <= '~'));

    private sealed class ServerReportRow
    {
        public string Id { get; set; } = "";

        public string MapId { get; set; } = "";

        public string? Build { get; set; }

        public string? NetworkCode { get; set; }

        public string CatalogVersion { get; set; } = "";

        public string? ReportedAt { get; set; }
    }
}
