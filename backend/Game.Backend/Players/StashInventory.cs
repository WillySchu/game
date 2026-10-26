using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Backend.Raids;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Players;

public enum StashOpsStatus
{
    Applied,
    PlayerNotFound,
    VersionConflict,
    Rejected,

    /// <summary>The player holds a raid ticket or is in a raid; their stash is locked until it resolves.</summary>
    InRaid,
}

/// <summary>What happened to a batch of stash operations.</summary>
public sealed class StashOpsResult
{
    public StashOpsStatus Status { get; private init; }

    /// <summary>The version after the batch; the current version on a conflict.</summary>
    public long ProfileVersion { get; private init; }

    public IReadOnlyList<CreatedItem> Created { get; private init; } = [];

    /// <summary>Why the batch was rejected; null otherwise.</summary>
    public InventoryError? Error { get; private init; }

    /// <summary>The rejected op, or null when the batch as a whole failed validation.</summary>
    public int? FailedOpIndex { get; private init; }

    /// <summary>The raid holding the player, when <see cref="Status"/> is InRaid.</summary>
    public string? RaidId { get; private init; }

    internal static StashOpsResult Locked(string raidId) => new() { Status = StashOpsStatus.InRaid, RaidId = raidId };

    internal static StashOpsResult Applied(long version, IReadOnlyList<CreatedItem> created) =>
        new() { Status = StashOpsStatus.Applied, ProfileVersion = version, Created = created };

    internal static StashOpsResult NotFound() => new() { Status = StashOpsStatus.PlayerNotFound };

    internal static StashOpsResult Conflict(long current) => new() { Status = StashOpsStatus.VersionConflict, ProfileVersion = current };

    internal static StashOpsResult Rejected(long current, int? opIndex, InventoryError error) =>
        new() { Status = StashOpsStatus.Rejected, ProfileVersion = current, FailedOpIndex = opIndex, Error = error };
}

/// <summary>
/// Applies a client's stash and equipment edits: the backend half of POST /v1/me/inventory/ops.
///
/// One write transaction checks the profile version, loads the player's stash and character into an
/// <see cref="InventoryTree"/>, applies the ops there (all or nothing, by the shared rules), writes the
/// resulting change list, bumps the version and records a ledger entry. The tree only holds this player's
/// two roots, so ops naming anyone else's items, or items in a raid, are simply "item_not_found".
/// </summary>
public sealed class StashInventory
{
    public const int MaxOpsPerRequest = 50;

    private readonly Database _db;
    private readonly ItemCatalog _catalog;
    private readonly TimeProvider _time;

    public StashInventory(Database db, ItemCatalog catalog, TimeProvider time)
    {
        _db = db;
        _catalog = catalog;
        _time = time;
    }

    public Task<StashOpsResult> ApplyAsync(string playerId, long expectedVersion, IReadOnlyList<InventoryOp> ops, CancellationToken ct = default) =>
        _db.WriteAsync(async (c, tx) =>
        {
            var player = (await c.QueryAsync<PlayerRoots>(
                "SELECT profile_version AS ProfileVersion, stash_root_id AS StashRootId, character_root_id AS CharacterRootId FROM players WHERE id = @playerId",
                new { playerId }, transaction: tx)).FirstOrDefault();
            if (player is null)
            {
                return StashOpsResult.NotFound();
            }

            // Gear in a raid is the game server's to move. Locking the stash until the raid resolves also keeps
            // a refund or extraction from landing in a slot the player has since filled.
            var raidId = await RaidService.LiveRaidIdAsync(c, tx, playerId, Timestamps.Format(_time.GetUtcNow()));
            if (raidId is not null)
            {
                return StashOpsResult.Locked(raidId);
            }

            if (player.ProfileVersion != expectedVersion)
            {
                return StashOpsResult.Conflict(player.ProfileVersion);
            }

            var items = await ItemRows.LoadStashItemsAsync(c, tx, playerId);
            var tree = new InventoryTree(_catalog, [items[player.StashRootId], items[player.CharacterRootId]]);
            var result = tree.Apply(ops);
            if (!result.Success)
            {
                return StashOpsResult.Rejected(player.ProfileVersion, result.FailedOpIndex, result.Error);
            }

            // A batch that changes nothing (say, dropping an item back where it was) costs no version or ledger entry.
            if (result.Changes.IsEmpty)
            {
                return StashOpsResult.Applied(player.ProfileVersion, result.Created);
            }

            var now = _time.GetUtcNow().UtcDateTime.ToString("O");
            await PersistAsync(c, tx, playerId, tree, ops, result, now);

            var newVersion = player.ProfileVersion + 1;
            var bumped = await c.ExecuteAsync(
                "UPDATE players SET profile_version = @newVersion WHERE id = @playerId AND profile_version = @expectedVersion",
                new { newVersion, playerId, expectedVersion }, transaction: tx);
            if (bumped != 1)
            {
                throw new InvalidOperationException($"Profile version of {playerId} changed mid-transaction.");
            }

            await c.ExecuteAsync(
                "INSERT INTO ledger_entries (player_id, kind, detail, created_at) VALUES (@playerId, 'inventory', @detail, @now)",
                new { playerId, detail = LedgerDetail(ops.Count, newVersion, result.Changes), now }, transaction: tx);

            return StashOpsResult.Applied(newVersion, result.Created);
        }, ct);

    private async Task PersistAsync(SqliteConnection c, SqliteTransaction tx, string playerId, InventoryTree tree, IReadOnlyList<InventoryOp> ops, ApplyResult result, string now)
    {
        var changes = result.Changes;

        // 1. Removed items leave the live states first, so they stop counting for the one-item-per-slot index.
        foreach (var (id, reason) in changes.Removed)
        {
            await ExpectOneRowAsync(c, tx, id, c.ExecuteAsync(
                "UPDATE item_instances SET state = @state, updated_at = @now WHERE id = @id AND owner_id = @playerId AND state = 'stash'",
                new { state = ItemStates.ForRemoval(reason), now, id, playerId }, transaction: tx));
        }

        // 2. Lift every moved item out of its old position. The unique index on (parent, named slot) is checked
        //    after each statement, so writing final positions in some order could briefly put two items in one
        //    slot even though the end state is valid. Clearing first makes the order irrelevant.
        foreach (var id in changes.Updated)
        {
            await ExpectOneRowAsync(c, tx, id, c.ExecuteAsync(
                "UPDATE item_instances SET parent_id = NULL, slot = NULL, grid_x = NULL, grid_y = NULL WHERE id = @id AND owner_id = @playerId AND state = 'stash'",
                new { id, playerId }, transaction: tx));
        }

        // 3. Write the final positions and quantities.
        foreach (var id in changes.Updated)
        {
            tree.TryGet(id, out var node);
            await c.ExecuteAsync(
                """
                UPDATE item_instances
                SET parent_id = @ParentId, slot = @Slot, grid_x = @GridX, grid_y = @GridY, rotated = @Rotated,
                    quantity = @Quantity, durability = @Durability, updated_at = @Now
                WHERE id = @Id
                """,
                new
                {
                    node!.Id,
                    ParentId = tree.ParentOf(id)!.Id,
                    node.Slot,
                    GridX = (long?)node.X,
                    GridY = (long?)node.Y,
                    Rotated = node.Rotated ? 1L : 0L,
                    Quantity = (long)node.Quantity,
                    node.Durability,
                    Now = now,
                },
                transaction: tx);
        }

        // 4. New stacks from splits. Each inherits its source stack's origin, so loot stays traceable to its raid.
        foreach (var created in result.Created)
        {
            tree.TryGet(created.ItemId, out var node);
            var source = (await c.QueryAsync<Origin>(
                "SELECT origin AS Value, origin_raid_id AS RaidId FROM item_instances WHERE id = @id",
                new { id = ops[created.OpIndex].ItemId }, transaction: tx)).Single();

            await ItemRows.InsertTreeAsync(c, tx, node!, tree.ParentOf(created.ItemId)!.Id, playerId, ItemStates.Stash, source.Value, now, source.RaidId);
        }
    }

    private static async Task ExpectOneRowAsync(SqliteConnection c, SqliteTransaction tx, string id, Task<int> update)
    {
        if (await update != 1)
        {
            throw new InvalidOperationException($"Item {id} was not in the player's stash when the batch was written.");
        }
    }

    private static string LedgerDetail(int opCount, long version, ChangeSet changes) => JsonSerializer.Serialize(new
    {
        ProfileVersion = version,
        Ops = opCount,
        changes.Created,
        changes.Updated,
        Removed = changes.Removed.Select(r => new { Id = r.Key, State = ItemStates.ForRemoval(r.Value) }),
    }, JsonDefaults.Options);

    private sealed class PlayerRoots
    {
        public long ProfileVersion { get; set; }

        public string StashRootId { get; set; } = "";

        public string CharacterRootId { get; set; } = "";
    }

    private sealed class Origin
    {
        public string Value { get; set; } = "";

        public string? RaidId { get; set; }
    }
}
