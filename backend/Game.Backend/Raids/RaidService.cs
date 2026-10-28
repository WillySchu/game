using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RaidZero.Backend.Auth;
using RaidZero.Backend.Data;
using RaidZero.Shared.Catalog;

namespace RaidZero.Backend.Raids;

/// <summary>
/// The raid lifecycle, from ticket to close. See "Raid Zero: Backend API &amp; Data Model" for the flow.
///
/// Rules every method here keeps:
/// - Items move between a stash and a raid's pool only inside one write transaction, with conditional updates
///   on (state, raid_id) and a row-count check, so an item can't be claimed twice or appear in two places.
/// - The character container never leaves its player. Checkout moves its contents into the raid and unhooks
///   the top-level ones from it, so gear lost in a raid can never block the player re-equipping afterwards.
/// - While a player has a live ticket or is in a raid, their stash and equipment are locked (see
///   <see cref="LiveRaidIdAsync"/>), which keeps refunds and extractions from colliding with newer gear.
/// </summary>
public sealed partial class RaidService
{
    private readonly Database _db;
    private readonly ItemCatalog _catalog;
    private readonly GameServerRegistry _servers;
    private readonly RaidOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RaidService> _log;

    public RaidService(Database db, ItemCatalog catalog, GameServerRegistry servers, IOptions<BackendOptions> options, TimeProvider time, ILogger<RaidService> log)
    {
        _db = db;
        _catalog = catalog;
        _servers = servers;
        _options = options.Value.Raids;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// The raid a player is currently committed to: a ticket that hasn't expired, or a raid they joined and
    /// haven't left. Null when they are free.
    /// </summary>
    public static async Task<string?> LiveRaidIdAsync(SqliteConnection c, SqliteTransaction? tx, string playerId, string now) =>
        (await c.QueryAsync<string>(
            """
            SELECT raid_id FROM raid_participants
            WHERE player_id = @playerId AND (state = 'in_raid' OR (state = 'ticketed' AND ticket_expires_at > @now))
            """,
            new { playerId, now }, transaction: tx)).FirstOrDefault();

    private string Now() => Timestamps.Format(_time.GetUtcNow());

    private static string HashJoinToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool IsLive(string raidState) => raidState is RaidStates.Pending or RaidStates.Active;

    private static async Task<RaidRow?> GetRaidAsync(SqliteConnection c, SqliteTransaction? tx, string raidId) =>
        (await c.QueryAsync<RaidRow>(
            "SELECT id AS Id, map_id AS MapId, server_id AS ServerId, state AS State, lease_expires_at AS LeaseExpiresAt FROM raids WHERE id = @raidId",
            new { raidId }, transaction: tx)).FirstOrDefault();

    private static async Task<ParticipantRow?> GetParticipantAsync(SqliteConnection c, SqliteTransaction? tx, string raidId, string playerId) =>
        (await c.QueryAsync<ParticipantRow>(
            $"SELECT {ParticipantRow.Columns} FROM raid_participants WHERE raid_id = @raidId AND player_id = @playerId",
            new { raidId, playerId }, transaction: tx)).FirstOrDefault();

    private static async Task<PlayerRow?> GetPlayerAsync(SqliteConnection c, SqliteTransaction? tx, string playerId) =>
        (await c.QueryAsync<PlayerRow>(
            "SELECT id AS Id, display_name AS DisplayName, stash_root_id AS StashRootId, character_root_id AS CharacterRootId FROM players WHERE id = @playerId",
            new { playerId }, transaction: tx)).FirstOrDefault();

    /// <summary>Tells clients their profile changed: items arrived or left outside a stash edit.</summary>
    private static Task BumpProfileAsync(SqliteConnection c, SqliteTransaction tx, string playerId) =>
        c.ExecuteAsync("UPDATE players SET profile_version = profile_version + 1 WHERE id = @playerId", new { playerId }, transaction: tx);

    private static Task LedgerAsync(SqliteConnection c, SqliteTransaction tx, string? playerId, string raidId, string kind, object detail, string now) =>
        c.ExecuteAsync(
            "INSERT INTO ledger_entries (player_id, raid_id, kind, detail, created_at) VALUES (@playerId, @raidId, @kind, @detail, @now)",
            new { playerId, raidId, kind, detail = System.Text.Json.JsonSerializer.Serialize(detail, JsonDefaults.Options), now },
            transaction: tx);

    private static ServerResponse RaidNotFound() =>
        ServerResponse.Error(404, "raid_not_found", "No such raid, or it isn't assigned to this server.");

    private static ServerResponse RaidNotActive(string state) =>
        ServerResponse.Error(409, "raid_not_active", $"The raid is {state}.", new { State = state });

    private sealed class RaidRow
    {
        public string Id { get; set; } = "";

        public string MapId { get; set; } = "";

        public string ServerId { get; set; } = "";

        public string State { get; set; } = "";

        public string LeaseExpiresAt { get; set; } = "";

        public string? StartedAt { get; set; }
    }

    private sealed class ParticipantRow
    {
        public const string Columns =
            "raid_id AS RaidId, player_id AS PlayerId, state AS State, join_token_hash AS JoinTokenHash, " +
            "ticket_expires_at AS TicketExpiresAt, loadout_snapshot AS LoadoutSnapshot";

        public string RaidId { get; set; } = "";

        public string PlayerId { get; set; } = "";

        public string State { get; set; } = "";

        public string JoinTokenHash { get; set; } = "";

        public string TicketExpiresAt { get; set; } = "";

        public string? LoadoutSnapshot { get; set; }
    }

    private sealed class PlayerRow
    {
        public string Id { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public string StashRootId { get; set; } = "";

        public string CharacterRootId { get; set; } = "";
    }
}

public static class RaidStates
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Closed = "closed";
    public const string Aborted = "aborted";
}

public static class ParticipantStates
{
    public const string Ticketed = "ticketed";
    public const string InRaid = "in_raid";
    public const string Extracted = "extracted";
    public const string Died = "died";
    public const string Refunded = "refunded";
    public const string Expired = "expired";
}
