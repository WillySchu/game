using System.Text.Json;
using Dapper;
using RaidZero.Backend.Data;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    /// <param name="Outcome">The player's state in the raid: ticketed, in_raid, extracted, died, refunded or expired.</param>
    public sealed record RaidResult(
        string RaidId, string MapId, string RaidState, string Outcome,
        IReadOnlyList<string> ItemsGained, IReadOnlyList<string> ItemsReturned, IReadOnlyList<string> ItemsLost);

    /// <summary>
    /// GET /v1/me/raids/{raid_id}: what a raid did to the player's gear, for the post-raid screen.
    /// Returned items came home, gained items are new loot, lost items stayed behind.
    /// </summary>
    public Task<ServerResponse> GetResultAsync(string playerId, string raidId, CancellationToken ct = default) =>
        _db.ReadAsync(async c =>
        {
            var participant = await GetParticipantAsync(c, null, raidId, playerId);
            var raid = await GetRaidAsync(c, null, raidId);
            if (participant is null || raid is null)
            {
                return ServerResponse.Error(404, "raid_not_found", "You weren't part of that raid.");
            }

            var gained = new List<string>();
            var returned = new List<string>();
            foreach (var entry in await c.QueryAsync<LedgerRow>(
                "SELECT kind AS Kind, detail AS Detail FROM ledger_entries WHERE player_id = @playerId AND raid_id = @raidId AND kind IN ('extract', 'refund')",
                new { playerId, raidId }))
            {
                var detail = JsonDocument.Parse(entry.Detail).RootElement;
                if (entry.Kind == "extract")
                {
                    gained.AddRange(detail.GetProperty("gained").EnumerateArray().Select(e => e.GetString()!));
                    returned.AddRange(detail.GetProperty("returned").EnumerateArray().Select(e => e.GetString()!));
                }
                else
                {
                    returned.AddRange(detail.GetProperty("items").EnumerateArray().Select(e => e.GetString()!));
                }
            }

            var lost = new List<string>();
            var finished = participant.State is ParticipantStates.Extracted or ParticipantStates.Died or ParticipantStates.Refunded;
            if (finished && participant.LoadoutSnapshot is not null)
            {
                var brought = JsonSerializer.Deserialize<ItemNode>(participant.LoadoutSnapshot, JsonDefaults.Options)!.Walk().Skip(1).Select(n => n.Id);
                var home = returned.ToHashSet(StringComparer.Ordinal);
                lost.AddRange(brought.Where(id => !home.Contains(id)));
            }

            return ServerResponse.Json(200, new RaidResult(raidId, raid.MapId, raid.State, participant.State, gained, returned, lost));
        }, ct);

    private sealed class LedgerRow
    {
        public string Kind { get; set; } = "";

        public string Detail { get; set; } = "";
    }
}
