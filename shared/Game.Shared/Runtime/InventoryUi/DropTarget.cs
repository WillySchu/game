namespace RaidZero.Shared.InventoryUi
{
    public enum DropTargetKind
    {
        /// <summary>Nowhere the item can go: over the screen's background, say. Releasing puts it back.</summary>
        None,

        /// <summary>A cell of one of a container's grids: the cell the dragged item's top-left corner would land on.</summary>
        GridCell,

        /// <summary>A named slot, such as the character's primary weapon slot.</summary>
        Slot,

        /// <summary>Another item: merge into it, load into it, or put inside it.</summary>
        Item,

        /// <summary>Outside the inventory window: drop it on the ground.</summary>
        Outside,
    }

    /// <summary>What's under the cursor while dragging, as the screen works it out from what it drew.</summary>
    public sealed class DropTarget
    {
        private DropTarget(DropTargetKind kind, string parentId, string slot, int x, int y, string itemId)
        {
            Kind = kind;
            ParentId = parentId;
            Slot = slot;
            X = x;
            Y = y;
            ItemId = itemId;
        }

        public static DropTarget None { get; } = new DropTarget(DropTargetKind.None, null, null, 0, 0, null);

        public static DropTarget Outside { get; } = new DropTarget(DropTargetKind.Outside, null, null, 0, 0, null);

        public DropTargetKind Kind { get; }

        /// <summary>For a grid cell or slot: the item whose grid or slot it is.</summary>
        public string ParentId { get; }

        /// <summary>For a slot: its name. For a grid cell: the grid's name ("grid", or a pocket's).</summary>
        public string Slot { get; }

        /// <summary>For a grid cell: the column and row. They can be out of the grid's bounds (shown as refused).</summary>
        public int X { get; }

        public int Y { get; }

        /// <summary>For an item: the item under the cursor.</summary>
        public string ItemId { get; }

        public static DropTarget GridCell(string containerId, string gridName, int x, int y) => new DropTarget(DropTargetKind.GridCell, containerId, gridName, x, y, null);

        public static DropTarget NamedSlot(string parentId, string slot) => new DropTarget(DropTargetKind.Slot, parentId, slot, 0, 0, null);

        public static DropTarget OnItem(string itemId) => new DropTarget(DropTargetKind.Item, null, null, 0, 0, itemId);

        public override string ToString()
        {
            switch (Kind)
            {
                case DropTargetKind.GridCell:
                    return "cell (" + X + "," + Y + ") of " + ParentId + "'s " + Slot;
                case DropTargetKind.Slot:
                    return "slot " + Slot + " of " + ParentId;
                case DropTargetKind.Item:
                    return "item " + ItemId;
                default:
                    return Kind.ToString();
            }
        }
    }
}
