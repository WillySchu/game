using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Data;

/// <summary>
/// Converts between item_instances rows and <see cref="ItemNode"/> trees. Every query names its columns
/// with aliases (def_id AS DefId), so no global Dapper mapping setting is needed.
/// </summary>
public static class ItemRows
{
    private const string Columns =
        "id AS Id, def_id AS DefId, parent_id AS ParentId, slot AS Slot, grid_x AS GridX, grid_y AS GridY, " +
        "rotated AS Rotated, quantity AS Quantity, durability AS Durability";

    /// <summary>Inserts a whole tree, root first. Every item gets the same owner, state and origin.</summary>
    public static async Task<List<string>> InsertTreeAsync(
        SqliteConnection connection, SqliteTransaction tx, ItemNode root, string? parentId,
        string ownerId, string state, string origin, string now)
    {
        var inserted = new List<string>();
        var pending = new Stack<(ItemNode Node, string? ParentId)>();
        pending.Push((root, parentId));

        while (pending.Count > 0)
        {
            var (node, parent) = pending.Pop();
            await connection.ExecuteAsync(
                """
                INSERT INTO item_instances
                    (id, def_id, owner_id, state, parent_id, slot, grid_x, grid_y, rotated, quantity, durability, origin, created_at, updated_at)
                VALUES
                    (@Id, @DefId, @OwnerId, @State, @ParentId, @Slot, @GridX, @GridY, @Rotated, @Quantity, @Durability, @Origin, @Now, @Now)
                """,
                new
                {
                    node.Id,
                    node.DefId,
                    OwnerId = ownerId,
                    State = state,
                    ParentId = parent,
                    node.Slot,
                    GridX = (long?)node.X,
                    GridY = (long?)node.Y,
                    Rotated = node.Rotated ? 1L : 0L,
                    Quantity = (long)node.Quantity,
                    node.Durability,
                    Origin = origin,
                    Now = now,
                },
                transaction: tx);
            inserted.Add(node.Id);

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                pending.Push((node.Children[i], node.Id));
            }
        }

        return inserted;
    }

    /// <summary>
    /// Loads every item a player holds outside raids (state 'stash') and links them into trees.
    /// Returns them by ID; the roots are the ones named on the player row.
    /// </summary>
    public static async Task<Dictionary<string, ItemNode>> LoadStashItemsAsync(SqliteConnection connection, SqliteTransaction? tx, string ownerId)
    {
        var rows = (await connection.QueryAsync<Row>(
            $"SELECT {Columns} FROM item_instances WHERE owner_id = @ownerId AND state = @state ORDER BY id",
            new { ownerId, state = ItemStates.Stash },
            transaction: tx)).ToList();

        var byId = rows.ToDictionary(r => r.Id, ToNode, StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.ParentId is not null))
        {
            if (!byId.TryGetValue(row.ParentId!, out var parent))
            {
                throw new InvalidOperationException(
                    $"Item {row.Id} of player {ownerId} sits in {row.ParentId}, which isn't in their stash. The data is inconsistent.");
            }

            parent.Children.Add(byId[row.Id]);
        }

        return byId;
    }

    private static ItemNode ToNode(Row row) => new()
    {
        Id = row.Id,
        DefId = row.DefId,
        Slot = row.Slot,
        X = (int?)row.GridX,
        Y = (int?)row.GridY,
        Rotated = row.Rotated != 0,
        Quantity = (int)row.Quantity,
        Durability = row.Durability,
    };

    // SQLite hands integers back as 64-bit, so the row type uses long throughout and converts once here.
    private sealed class Row
    {
        public string Id { get; set; } = "";

        public string DefId { get; set; } = "";

        public string? ParentId { get; set; }

        public string? Slot { get; set; }

        public long? GridX { get; set; }

        public long? GridY { get; set; }

        public long Rotated { get; set; }

        public long Quantity { get; set; }

        public double? Durability { get; set; }
    }
}
