using System;
using System.Collections.Generic;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;

namespace RaidZero.Shared.Inventory
{
    /// <summary>
    /// Checks that an item tree obeys the catalog: every item is known, sits somewhere it is allowed,
    /// fits, doesn't overlap, and has a sane quantity and durability.
    ///
    /// The backend runs this on every commit (stash edits, checkout, extraction). The game server runs
    /// it before sending a report, so a bug shows up on the server where it happened rather than as a
    /// rejected report. Trees from a trusted server should always pass; a failure means a bug.
    /// </summary>
    public static class InventoryValidator
    {
        public const int DefaultMaxDepth = 8;

        public static IReadOnlyList<InventoryError> Validate(ItemCatalog catalog, ItemNode root, int maxDepth = DefaultMaxDepth)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var context = new Context(catalog, maxDepth);
            if (root == null)
            {
                context.Add(null, InventoryErrorCodes.NullItem, "The root item is missing.");
                return context.Errors;
            }

            Visit(context, root, null, null, 0);
            return context.Errors;
        }

        public static bool IsValid(ItemCatalog catalog, ItemNode root)
        {
            return Validate(catalog, root).Count == 0;
        }

        private static void Visit(Context ctx, ItemNode node, ItemNode parent, ItemDefinition parentDef, int depth)
        {
            if (!Uuid7.IsValid(node.Id))
            {
                ctx.Add(node.Id, InventoryErrorCodes.InvalidId, "Item ID '" + node.Id + "' is not a lowercase canonical UUID.");
            }
            else if (!ctx.Ids.Add(node.Id))
            {
                ctx.Add(node.Id, InventoryErrorCodes.DuplicateId, "Item ID appears more than once in the tree.");
            }

            if (!ctx.Catalog.TryGet(node.DefId, out var def))
            {
                ctx.Add(node.Id, InventoryErrorCodes.UnknownDef, "Unknown def_id '" + node.DefId + "'.");
                return;
            }

            CheckQuantityAndDurability(ctx, node, def);
            CheckPlacement(ctx, node, def, parent, parentDef);

            if (node.Children == null || node.Children.Count == 0)
            {
                return;
            }

            if (depth >= ctx.MaxDepth)
            {
                ctx.Add(node.Id, InventoryErrorCodes.TooDeep, "Items are nested more than " + ctx.MaxDepth + " levels deep.");
                return;
            }

            CheckNamedSlotOccupancy(ctx, node, def);
            CheckGridLayout(ctx, node, def);

            foreach (var child in node.Children)
            {
                if (child == null)
                {
                    ctx.Add(node.Id, InventoryErrorCodes.NullItem, "A child of this item is missing.");
                    continue;
                }

                Visit(ctx, child, node, def, depth + 1);
            }
        }

        private static void CheckQuantityAndDurability(Context ctx, ItemNode node, ItemDefinition def)
        {
            if (node.Quantity < 1 || node.Quantity > def.MaxStack)
            {
                ctx.Add(node.Id, InventoryErrorCodes.QuantityOutOfRange,
                    "Quantity " + node.Quantity + " is outside 1.." + def.MaxStack + " for " + def.DefId + ".");
            }

            if (def.HasDurability)
            {
                if (node.Durability == null || double.IsNaN(node.Durability.Value) || node.Durability < 0 || node.Durability > 1)
                {
                    ctx.Add(node.Id, InventoryErrorCodes.DurabilityInvalid, def.DefId + " needs a durability between 0 and 1.");
                }
            }
            else if (node.Durability != null)
            {
                ctx.Add(node.Id, InventoryErrorCodes.DurabilityInvalid, def.DefId + " has no durability, but one was given.");
            }
        }

        private static void CheckPlacement(Context ctx, ItemNode node, ItemDefinition def, ItemNode parent, ItemDefinition parentDef)
        {
            if (parent == null)
            {
                if (node.Slot != null || node.X != null || node.Y != null || node.Rotated)
                {
                    ctx.Add(node.Id, InventoryErrorCodes.PositionMismatch, "A root item cannot have a slot or a grid position.");
                }

                return;
            }

            if (parentDef.FindGrid(node.Slot) != null)
            {
                if (node.X == null || node.Y == null)
                {
                    ctx.Add(node.Id, InventoryErrorCodes.PositionMismatch, "Items in a grid need x and y.");
                }

                return;
            }

            if (node.Slot == SlotNames.Grid)
            {
                ctx.Add(node.Id, InventoryErrorCodes.SlotMismatch, parentDef.DefId + " has no grid named 'grid'.");
                return;
            }

            SlotSpec spec = null;
            if (node.Slot != null && parentDef.Slots != null)
            {
                spec = parentDef.Slots.Find(s => s.Name == node.Slot);
            }

            if (spec == null)
            {
                ctx.Add(node.Id, InventoryErrorCodes.SlotMismatch, parentDef.DefId + " has no slot '" + node.Slot + "'.");
                return;
            }

            if (!Accepts(spec, def))
            {
                ctx.Add(node.Id, InventoryErrorCodes.SlotMismatch, "Slot '" + spec.Name + "' does not accept " + def.DefId + ".");
            }

            if (node.X != null || node.Y != null || node.Rotated)
            {
                ctx.Add(node.Id, InventoryErrorCodes.PositionMismatch, "Items in a named slot cannot have x, y or rotation.");
            }

            if (spec.MaxQuantity > 0 && node.Quantity > spec.MaxQuantity)
            {
                ctx.Add(node.Id, InventoryErrorCodes.QuantityOutOfRange,
                    "Slot '" + spec.Name + "' holds at most " + spec.MaxQuantity + ", got " + node.Quantity + ".");
            }
        }

        private static void CheckNamedSlotOccupancy(Context ctx, ItemNode container, ItemDefinition containerDef)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in container.Children)
            {
                if (child == null || child.Slot == null || containerDef.FindGrid(child.Slot) != null)
                {
                    continue;
                }

                if (!used.Add(child.Slot))
                {
                    ctx.Add(child.Id, InventoryErrorCodes.SlotOccupied, "Slot '" + child.Slot + "' already holds another item.");
                }
            }
        }

        private static void CheckGridLayout(Context ctx, ItemNode container, ItemDefinition containerDef)
        {
            foreach (var grid in containerDef.Grids ?? new List<GridSpec>())
            {
                if (grid != null)
                {
                    CheckGridLayout(ctx, container, grid);
                }
            }
        }

        // One grid at a time: items only collide with items in the same grid.
        private static void CheckGridLayout(Context ctx, ItemNode container, GridSpec grid)
        {
            var owner = new string[grid.Width, grid.Height];
            foreach (var child in container.Children)
            {
                if (child == null || child.Slot != grid.Name || child.X == null || child.Y == null)
                {
                    continue;
                }

                if (!ctx.Catalog.TryGet(child.DefId, out var childDef))
                {
                    continue;
                }

                GridPlacement.GetSize(childDef, child.Rotated, out int w, out int h);
                int x = child.X.Value;
                int y = child.Y.Value;
                if (x < 0 || y < 0 || x + w > grid.Width || y + h > grid.Height)
                {
                    ctx.Add(child.Id, InventoryErrorCodes.OutOfBounds,
                        childDef.DefId + " at (" + x + "," + y + ") does not fit in a " + grid.Width + "x" + grid.Height + " grid.");
                    continue;
                }

                bool overlaps = false;
                for (int cx = x; cx < x + w && !overlaps; cx++)
                {
                    for (int cy = y; cy < y + h; cy++)
                    {
                        if (owner[cx, cy] != null)
                        {
                            ctx.Add(child.Id, InventoryErrorCodes.Overlap, "Overlaps item " + owner[cx, cy] + ".");
                            overlaps = true;
                            break;
                        }
                    }
                }

                if (overlaps)
                {
                    continue;
                }

                for (int cx = x; cx < x + w; cx++)
                {
                    for (int cy = y; cy < y + h; cy++)
                    {
                        owner[cx, cy] = child.Id ?? "?";
                    }
                }
            }
        }

        internal static bool Accepts(SlotSpec spec, ItemDefinition def)
        {
            if (spec.Accepts == null || def.Tags == null)
            {
                return false;
            }

            foreach (var tag in def.Tags)
            {
                if (spec.Accepts.Contains(tag))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class Context
        {
            public Context(ItemCatalog catalog, int maxDepth)
            {
                Catalog = catalog;
                MaxDepth = maxDepth;
            }

            public ItemCatalog Catalog { get; }

            public int MaxDepth { get; }

            public HashSet<string> Ids { get; } = new HashSet<string>(StringComparer.Ordinal);

            public List<InventoryError> Errors { get; } = new List<InventoryError>();

            public void Add(string itemId, string code, string message)
            {
                Errors.Add(new InventoryError(itemId, code, message));
            }
        }
    }

    public sealed class InventoryError
    {
        public InventoryError(string itemId, string code, string message)
        {
            ItemId = itemId;
            Code = code;
            Message = message;
        }

        /// <summary>The item the problem is on, when there is one.</summary>
        public string ItemId { get; }

        /// <summary>A value from <see cref="InventoryErrorCodes"/>; also returned as the API's "reason".</summary>
        public string Code { get; }

        public string Message { get; }

        public override string ToString()
        {
            return Code + " (" + (ItemId ?? "no id") + "): " + Message;
        }
    }

    /// <summary>Machine-readable error codes. The backend returns these as the "reason" in 422 responses.</summary>
    public static class InventoryErrorCodes
    {
        public const string NullItem = "null_item";
        public const string InvalidId = "invalid_id";
        public const string DuplicateId = "duplicate_id";
        public const string UnknownDef = "unknown_def";
        public const string QuantityOutOfRange = "quantity_out_of_range";
        public const string DurabilityInvalid = "durability_invalid";
        public const string SlotMismatch = "slot_mismatch";
        public const string SlotOccupied = "slot_occupied";
        public const string PositionMismatch = "position_mismatch";
        public const string OutOfBounds = "out_of_bounds";
        public const string Overlap = "overlap";
        public const string TooDeep = "too_deep";

        // Returned by InventoryTree operations.
        public const string UnknownOp = "unknown_op";
        public const string ItemNotFound = "item_not_found";
        public const string TargetNotFound = "target_not_found";
        public const string RootItem = "root_item";
        public const string Cycle = "cycle";
        public const string NoSpace = "no_space";
        public const string NotStackable = "not_stackable";
        public const string StackMismatch = "stack_mismatch";
        public const string StackFull = "stack_full";
        public const string InvalidQuantity = "invalid_quantity";
    }
}
