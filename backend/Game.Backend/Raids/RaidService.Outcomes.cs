using System.Text.Json;
using Dapper;
using RaidZero.Backend.Data;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    public sealed record OutcomeRequest(string? PlayerId, string? Outcome, string? ExtractPoint, ItemNode? Inventory, JsonElement? Stats);

    public sealed record OutcomeResponse(bool Committed, int ItemsGained, int ItemsReturned);

    public sealed record CloseRequest(string? Reason);

    public sealed record CloseResponse(bool Closed, int ItemsLost, int ParticipantsDied);

    /// <summary>
    /// POST /v1/server/raids/{raid_id}/outcomes: one player extracted or died.
    ///
    /// A death carries no items. The player's gear stays in the raid's pool, where anyone else can still
    /// extract it, and whatever nobody claims is lost when the raid closes.
    ///
    /// An extraction carries the player's whole tree, rooted at their character. Each item with a known ID
    /// must be in this raid's pool and is claimed with a conditional update, so it can't be claimed twice.
    /// Unknown IDs are new raid loot minted by the server. All or nothing: one bad item rejects the report.
    /// </summary>
    public Task<ServerResponse> ReportOutcomeAsync(string serverId, string raidId, string idempotencyKey, string requestHash, OutcomeRequest request, CancellationToken ct = default) =>
        Idempotency.RunAsync(_db, serverId, idempotencyKey, requestHash, _time, async (c, tx) =>
        {
            var now = Now();
            var raid = await GetRaidAsync(c, tx, raidId);
            if (raid is null || raid.ServerId != serverId)
            {
                return RaidNotFound();
            }

            if (raid.State != RaidStates.Active)
            {
                return RaidNotActive(raid.State);
            }

            if (request.PlayerId is null || request.Outcome is not ("extracted" or "died"))
            {
                return ServerResponse.Error(422, "invalid_request", "player_id and an outcome of 'extracted' or 'died' are required.");
            }

            var participant = await GetParticipantAsync(c, tx, raidId, request.PlayerId);
            if (participant is null)
            {
                return ServerResponse.Error(404, "participant_not_found", "That player isn't part of this raid.");
            }

            if (participant.State != ParticipantStates.InRaid)
            {
                return ServerResponse.Error(409, "participant_not_in_raid", $"That player is {participant.State}, not in the raid.", new { participant.State });
            }

            if (request.Outcome == "died")
            {
                await ResolveParticipantAsync(c, tx, raidId, request.PlayerId, ParticipantStates.Died, now);
                await LedgerAsync(c, tx, request.PlayerId, raidId, "death", new { request.ExtractPoint }, now);
                return ServerResponse.Json(200, new OutcomeResponse(true, 0, 0));
            }

            var player = (await GetPlayerAsync(c, tx, request.PlayerId))!;
            var inventory = request.Inventory;
            if (inventory is null || inventory.Id != player.CharacterRootId || inventory.DefId != WellKnownDefs.Character)
            {
                return ServerResponse.Error(422, "invalid_inventory", "An extraction needs the player's inventory, rooted at their character.");
            }

            var invalid = InventoryValidator.Validate(_catalog, inventory);
            if (invalid.Count > 0)
            {
                return ServerResponse.Error(422, "invalid_inventory", invalid[0].Message, new { Reason = invalid[0].Code, invalid[0].ItemId });
            }

            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in inventory.Walk())
            {
                foreach (var child in node.Children)
                {
                    parentOf[child.Id] = node.Id;
                }
            }

            var carried = inventory.Walk().Skip(1).ToList();
            var ids = carried.Select(n => n.Id).ToList();
            var known = (await c.QueryAsync<PoolRow>(
                "SELECT id AS Id, def_id AS DefId, state AS State, raid_id AS RaidId FROM item_instances WHERE id IN @ids",
                new { ids }, transaction: tx)).ToDictionary(r => r.Id, StringComparer.Ordinal);

            foreach (var node in carried.Where(n => known.ContainsKey(n.Id)))
            {
                var row = known[node.Id];
                if (row.State != ItemStates.InRaid || row.RaidId != raidId)
                {
                    return ServerResponse.Error(409, "item_not_in_raid_pool", $"Item {node.Id} isn't in this raid's pool.", new { ItemId = node.Id });
                }

                if (row.DefId != node.DefId)
                {
                    return ServerResponse.Error(409, "item_mismatch", $"Item {node.Id} is a {row.DefId}, not a {node.DefId}.", new { ItemId = node.Id });
                }
            }

            var claimed = carried.Where(n => known.ContainsKey(n.Id)).ToList();
            var loot = carried.Where(n => !known.ContainsKey(n.Id)).ToList();
            var claimedIds = claimed.Select(n => n.Id).ToList();

            // Phase 1: unhook the claimed items, and anything still in the pool hanging off them (a magazine
            // dropped from a rifle, say), from their old positions. The one-item-per-slot index is checked after
            // every statement, so without this a valid tree could collide with its own past positions.
            if (claimedIds.Count > 0)
            {
                await c.ExecuteAsync(
                    """
                    UPDATE item_instances SET parent_id = NULL, slot = NULL, grid_x = NULL, grid_y = NULL
                    WHERE raid_id = @raidId AND state = 'in_raid' AND (id IN @claimedIds OR parent_id IN @claimedIds)
                    """,
                    new { raidId, claimedIds }, transaction: tx);
            }

            // Phase 2: claim each item into the player's stash at its reported position.
            foreach (var node in claimed)
            {
                var rows = await c.ExecuteAsync(
                    """
                    UPDATE item_instances
                    SET state = 'stash', owner_id = @playerId, raid_id = NULL, parent_id = @parentId, slot = @Slot,
                        grid_x = @GridX, grid_y = @GridY, rotated = @Rotated, quantity = @Quantity, durability = @Durability, updated_at = @now
                    WHERE id = @Id AND state = 'in_raid' AND raid_id = @raidId
                    """,
                    new
                    {
                        playerId = player.Id,
                        parentId = parentOf[node.Id],
                        node.Slot,
                        GridX = (long?)node.X,
                        GridY = (long?)node.Y,
                        Rotated = node.Rotated ? 1L : 0L,
                        Quantity = (long)node.Quantity,
                        node.Durability,
                        now,
                        node.Id,
                        raidId,
                    },
                    transaction: tx);
                if (rows != 1)
                {
                    throw new RaidRejection(ServerResponse.Error(409, "item_not_in_raid_pool", $"Item {node.Id} was claimed by someone else.", new { ItemId = node.Id }));
                }
            }

            foreach (var node in loot)
            {
                await ItemRows.InsertItemAsync(c, tx, node, parentOf[node.Id], player.Id, ItemStates.Stash, "raid_loot", now, raidId);
            }

            await ResolveParticipantAsync(c, tx, raidId, player.Id, ParticipantStates.Extracted, now);
            await BumpProfileAsync(c, tx, player.Id);
            await LedgerAsync(c, tx, player.Id, raidId, "extract",
                new { request.ExtractPoint, Gained = loot.Select(n => n.Id), Returned = claimedIds }, now);

            return ServerResponse.Json(200, new OutcomeResponse(true, loot.Count, claimed.Count));
        }, ct);

    /// <summary>
    /// POST /v1/server/raids/{raid_id}/close. The raid is over: anyone still inside dies, unredeemed tickets
    /// expire, and every item left in the pool is lost.
    /// </summary>
    public Task<ServerResponse> CloseAsync(string serverId, string raidId, string idempotencyKey, string requestHash, CancellationToken ct = default) =>
        Idempotency.RunAsync(_db, serverId, idempotencyKey, requestHash, _time, async (c, tx) =>
        {
            var now = Now();
            var raid = await GetRaidAsync(c, tx, raidId);
            if (raid is null || raid.ServerId != serverId)
            {
                return RaidNotFound();
            }

            if (!IsLive(raid.State))
            {
                return RaidNotActive(raid.State);
            }

            var died = (await c.QueryAsync<string>(
                "SELECT player_id FROM raid_participants WHERE raid_id = @raidId AND state = 'in_raid'", new { raidId }, transaction: tx)).ToList();
            await c.ExecuteAsync(
                "UPDATE raid_participants SET state = 'died', resolved_at = @now WHERE raid_id = @raidId AND state = 'in_raid'",
                new { now, raidId }, transaction: tx);
            await c.ExecuteAsync(
                "UPDATE raid_participants SET state = 'expired', resolved_at = @now WHERE raid_id = @raidId AND state = 'ticketed'",
                new { now, raidId }, transaction: tx);

            var lost = await LosePoolAsync(c, tx, raidId, now);
            await c.ExecuteAsync("UPDATE raids SET state = 'closed', closed_at = @now WHERE id = @raidId", new { now, raidId }, transaction: tx);
            await LedgerAsync(c, tx, null, raidId, "raid_close", new { ItemsLost = lost, Died = died }, now);

            return ServerResponse.Json(200, new CloseResponse(true, lost, died.Count));
        }, ct);

    private static Task ResolveParticipantAsync(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string raidId, string playerId, string state, string now) =>
        c.ExecuteAsync(
            "UPDATE raid_participants SET state = @state, resolved_at = @now WHERE raid_id = @raidId AND player_id = @playerId",
            new { state, now, raidId, playerId }, transaction: tx);

    /// <summary>Everything still in the raid's pool is gone for good.</summary>
    private static Task<int> LosePoolAsync(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string raidId, string now) =>
        c.ExecuteAsync(
            "UPDATE item_instances SET state = 'lost', raid_id = NULL, updated_at = @now WHERE raid_id = @raidId AND state = 'in_raid'",
            new { now, raidId }, transaction: tx);

    private sealed class PoolRow
    {
        public string Id { get; set; } = "";

        public string DefId { get; set; } = "";

        public string State { get; set; } = "";

        public string? RaidId { get; set; }
    }
}
