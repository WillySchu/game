using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Raids;

public sealed partial class RaidService
{
    /// <summary>
    /// Aborts every raid whose lease has run out: its game server crashed, hung or lost its connection.
    /// Called by <see cref="RaidReaper"/> on a timer; returns how many raids it aborted.
    /// </summary>
    public async Task<int> ReapAsync(CancellationToken ct = default)
    {
        var now = Now();
        var expired = (await _db.ReadAsync(c => c.QueryAsync<string>(
            "SELECT id FROM raids WHERE state IN ('pending', 'active') AND lease_expires_at <= @now", new { now }), ct)).ToList();

        var aborted = 0;
        foreach (var raidId in expired)
        {
            var done = await _db.WriteAsync(async (c, tx) =>
            {
                // Re-check under the write lock: a heartbeat or close may have landed since the read.
                var raid = await GetRaidAsync(c, tx, raidId);
                if (raid is null || !IsLive(raid.State) || string.CompareOrdinal(raid.LeaseExpiresAt, now) > 0)
                {
                    return false;
                }

                await AbortAsync(c, tx, raidId, now);
                return true;
            }, ct);

            if (done)
            {
                aborted++;
                _log.LogWarning("Aborted raid {RaidId}: its game server stopped sending heartbeats.", raidId);
            }
        }

        return aborted;
    }

    /// <summary>
    /// A crash shouldn't cost players their gear, so this is generous on purpose: everyone still in the raid
    /// gets back what they brought, as long as nobody else already extracted it. Players who extracted keep
    /// what they got; players who died aren't refunded; raid loot nobody extracted disappears.
    /// </summary>
    private async Task AbortAsync(SqliteConnection c, SqliteTransaction tx, string raidId, string now)
    {
        await c.ExecuteAsync(
            "UPDATE raid_participants SET state = 'expired', resolved_at = @now WHERE raid_id = @raidId AND state = 'ticketed'",
            new { now, raidId }, transaction: tx);

        var inside = (await c.QueryAsync<ParticipantRow>(
            $"SELECT {ParticipantRow.Columns} FROM raid_participants WHERE raid_id = @raidId AND state = 'in_raid'",
            new { raidId }, transaction: tx)).ToList();

        foreach (var participant in inside)
        {
            var refunded = await RefundAsync(c, tx, raidId, participant, now);
            await ResolveParticipantAsync(c, tx, raidId, participant.PlayerId, ParticipantStates.Refunded, now);
            await BumpProfileAsync(c, tx, participant.PlayerId);
            await LedgerAsync(c, tx, participant.PlayerId, raidId, "refund", new { Items = refunded }, now);
        }

        var lost = await LosePoolAsync(c, tx, raidId, now);
        await c.ExecuteAsync("UPDATE raids SET state = 'aborted', closed_at = @now WHERE id = @raidId", new { now, raidId }, transaction: tx);
        await LedgerAsync(c, tx, null, raidId, "raid_abort", new { Refunded = inside.Select(p => p.PlayerId), ItemsLost = lost }, now);
    }

    /// <summary>
    /// Puts a player's checked-out gear back where the snapshot says it was. Only items still in this raid's
    /// pool come back, so nothing another player extracted is cloned. An item whose container someone else
    /// took (a medkit left behind in a rig they grabbed) goes to the stash's first free spot instead, or stays
    /// lost if the stash is full.
    /// </summary>
    private async Task<List<string>> RefundAsync(SqliteConnection c, SqliteTransaction tx, string raidId, ParticipantRow participant, string now)
    {
        var refunded = new List<string>();
        if (participant.LoadoutSnapshot is null)
        {
            return refunded;
        }

        var snapshot = JsonSerializer.Deserialize<ItemNode>(participant.LoadoutSnapshot, JsonDefaults.Options)!;
        var snapshotIds = snapshot.Walk().Skip(1).Select(n => n.Id).ToList();
        if (snapshotIds.Count == 0)
        {
            return refunded;
        }

        var stillInPool = (await c.QueryAsync<string>(
            "SELECT id FROM item_instances WHERE id IN @snapshotIds AND raid_id = @raidId AND state = 'in_raid'",
            new { snapshotIds, raidId }, transaction: tx)).ToHashSet(StringComparer.Ordinal);

        var player = (await GetPlayerAsync(c, tx, participant.PlayerId))!;
        var stash = (await ItemRows.LoadStashItemsAsync(c, tx, player.Id))[player.StashRootId];

        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in snapshot.Walk())
        {
            foreach (var child in node.Children)
            {
                parentOf[child.Id] = node.Id;
            }
        }

        foreach (var node in snapshot.Walk().Skip(1).Where(n => stillInPool.Contains(n.Id)))
        {
            var parent = parentOf[node.Id];
            string parentId;
            string? slot;
            int? x, y;
            bool rotated;

            if (parent == snapshot.Id || stillInPool.Contains(parent))
            {
                (parentId, slot, x, y, rotated) = (parent, node.Slot, node.X, node.Y, node.Rotated);
            }
            else if (_catalog.TryGet(node.DefId, out var def) && GridPlacement.TryFindFreeSpot(_catalog, stash, def, out var fgrid, out var fx, out var fy, out var frot))
            {
                (parentId, slot, x, y, rotated) = (stash.Id, fgrid, fx, fy, frot);
                stash.Children.Add(new ItemNode { Id = node.Id, DefId = node.DefId, Slot = fgrid, X = fx, Y = fy, Rotated = frot });
            }
            else
            {
                continue;
            }

            var rows = await c.ExecuteAsync(
                """
                UPDATE item_instances
                SET state = 'stash', owner_id = @playerId, raid_id = NULL, parent_id = @parentId, slot = @slot,
                    grid_x = @x, grid_y = @y, rotated = @rotated, updated_at = @now
                WHERE id = @id AND raid_id = @raidId AND state = 'in_raid'
                """,
                new { playerId = player.Id, parentId, slot, x = (long?)x, y = (long?)y, rotated = rotated ? 1L : 0L, now, id = node.Id, raidId },
                transaction: tx);
            if (rows == 1)
            {
                refunded.Add(node.Id);
            }
        }

        return refunded;
    }
}
