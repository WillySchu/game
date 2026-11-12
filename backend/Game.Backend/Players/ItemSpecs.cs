using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Backend.Players;

/// <summary>
/// Builds real items from item specs: the item tree shape without IDs, as written in starter_kit.json and
/// traders.json. Grid items may leave out x and y to take the first free spot.
/// </summary>
public static class ItemSpecs
{
    /// <summary>A fresh copy of one spec, with new IDs. Its own slot and position are left for the caller to set.</summary>
    public static ItemNode Build(ItemCatalog catalog, ItemNode spec, Func<string> newId)
    {
        if (!catalog.TryGet(spec.DefId, out _))
        {
            throw new ItemSpecException($"unknown def_id '{spec.DefId}'.");
        }

        var node = Copy(spec, newId);
        AddChildren(catalog, node, spec.Children, newId);
        return node;
    }

    /// <summary>Adds fresh copies of the specs, with new IDs, under parent, placing grid items that have no position.</summary>
    public static void AddChildren(ItemCatalog catalog, ItemNode parent, IEnumerable<ItemNode>? specs, Func<string> newId)
    {
        foreach (var spec in specs ?? [])
        {
            if (!catalog.TryGet(spec.DefId, out var def))
            {
                throw new ItemSpecException($"unknown def_id '{spec.DefId}'.");
            }

            var child = Copy(spec, newId);

            // "grid" with no position: the first free spot in any of the parent's grids. A pocket's name with no
            // position: the first free spot in that pocket.
            if (child.X is null && child.Y is null && catalog.TryGet(parent.DefId, out var parentDef)
                && (child.Slot == SlotNames.Grid || parentDef.FindGrid(child.Slot) is not null))
            {
                string? grid = child.Slot;
                int x, y;
                bool rotated;
                var found = child.Slot == SlotNames.Grid
                    ? GridPlacement.TryFindFreeSpot(catalog, parent, def, out grid, out x, out y, out rotated)
                    : GridPlacement.TryFindFreeSpot(catalog, parent, child.Slot, def, out x, out y, out rotated);
                if (!found)
                {
                    throw new ItemSpecException($"no room for {spec.DefId} in {parent.DefId}.");
                }

                (child.Slot, child.X, child.Y, child.Rotated) = (grid, x, y, rotated);
            }

            parent.Children.Add(child);
            AddChildren(catalog, child, spec.Children, newId);
        }
    }

    private static ItemNode Copy(ItemNode spec, Func<string> newId) => new()
    {
        Id = newId(),
        DefId = spec.DefId,
        Slot = spec.Slot,
        X = spec.X,
        Y = spec.Y,
        Rotated = spec.Rotated,
        Quantity = spec.Quantity,
        Durability = spec.Durability,
    };
}

public sealed class ItemSpecException : Exception
{
    public ItemSpecException(string message) : base(message)
    {
    }
}
