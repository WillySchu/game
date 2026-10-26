namespace RaidZero.Shared.Inventory
{
    /// <summary>
    /// One inventory change requested by a client, or by the game server on a player's behalf.
    /// Wire shape (snake_case), matching POST /v1/me/inventory/ops:
    /// <code>
    /// { "op": "move",    "item_id": "...", "to": { "parent_id": "...", "slot": "grid", "x": 0, "y": 1, "rotated": false } }
    /// { "op": "split",   "item_id": "...", "quantity": 30, "to": { ... } }
    /// { "op": "merge",   "item_id": "...", "into_id": "...", "quantity": 0 }
    /// { "op": "discard", "item_id": "..." }
    /// </code>
    /// </summary>
    public sealed class InventoryOp
    {
        /// <summary>One of <see cref="InventoryOpKinds"/>.</summary>
        public string Op { get; set; }

        public string ItemId { get; set; }

        /// <summary>Where the item goes, for move and split.</summary>
        public InventoryTarget To { get; set; }

        /// <summary>
        /// Split: how many to take off into a new stack (at least 1, less than the whole stack).
        /// Merge: the most to move; 0 means as many as fit.
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>The stack to merge into, for merge.</summary>
        public string IntoId { get; set; }

        public static InventoryOp Move(string itemId, InventoryTarget to)
        {
            return new InventoryOp { Op = InventoryOpKinds.Move, ItemId = itemId, To = to };
        }

        public static InventoryOp Split(string itemId, int quantity, InventoryTarget to)
        {
            return new InventoryOp { Op = InventoryOpKinds.Split, ItemId = itemId, Quantity = quantity, To = to };
        }

        public static InventoryOp Merge(string itemId, string intoId, int quantity = 0)
        {
            return new InventoryOp { Op = InventoryOpKinds.Merge, ItemId = itemId, IntoId = intoId, Quantity = quantity };
        }

        public static InventoryOp Discard(string itemId)
        {
            return new InventoryOp { Op = InventoryOpKinds.Discard, ItemId = itemId };
        }
    }

    /// <summary>A position inside a container: a grid cell, or a named slot.</summary>
    public sealed class InventoryTarget
    {
        public string ParentId { get; set; }

        /// <summary>"grid" or a named slot such as "primary".</summary>
        public string Slot { get; set; }

        /// <summary>
        /// Grid cell. Leave both X and Y null in a grid to take the first free spot, which is what
        /// "quick move" and server-side pickups want.
        /// </summary>
        public int? X { get; set; }

        public int? Y { get; set; }

        public bool Rotated { get; set; }

        public static InventoryTarget Grid(string parentId, int x, int y, bool rotated = false)
        {
            return new InventoryTarget { ParentId = parentId, Slot = SlotNames.Grid, X = x, Y = y, Rotated = rotated };
        }

        /// <summary>The first free spot in the container's grid.</summary>
        public static InventoryTarget AnyGridSpot(string parentId)
        {
            return new InventoryTarget { ParentId = parentId, Slot = SlotNames.Grid };
        }

        public static InventoryTarget NamedSlot(string parentId, string slot)
        {
            return new InventoryTarget { ParentId = parentId, Slot = slot };
        }
    }

    public static class InventoryOpKinds
    {
        public const string Move = "move";
        public const string Split = "split";
        public const string Merge = "merge";
        public const string Discard = "discard";
    }
}
