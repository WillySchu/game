using System.Collections.Generic;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>What a drop would do.</summary>
    public enum DropAction
    {
        /// <summary>Nothing: no target, or the item is already exactly there.</summary>
        None,

        /// <summary>Move it to a grid cell or an empty slot.</summary>
        Move,

        /// <summary>Top up a stack of the same item.</summary>
        Merge,

        /// <summary>Load it into a slot of the item under the cursor: a magazine into a rifle, rounds into a magazine.</summary>
        Load,

        /// <summary>Put it inside the item under the cursor, which has a grid: into a backpack, say.</summary>
        PutInside,

        /// <summary>Drop it on the ground.</summary>
        Drop,
    }

    /// <summary>
    /// The answer to "what happens if I let go here?", worked out with the same rules the server applies:
    /// the ops to send, whether they'd be accepted (and why not), and what to highlight while dragging.
    /// </summary>
    public sealed class DropPlan
    {
        private static readonly IReadOnlyList<InventoryOp> NoOps = new InventoryOp[0];

        private DropPlan(DropAction action, IReadOnlyList<InventoryOp> ops, string error, string message)
        {
            Action = action;
            Ops = ops ?? NoOps;
            Error = error;
            Message = message;
        }

        public DropAction Action { get; }

        /// <summary>The ops to send. Empty for <see cref="DropAction.None"/> and <see cref="DropAction.Drop"/>.</summary>
        public IReadOnlyList<InventoryOp> Ops { get; }

        /// <summary>Why it would be refused: an InventoryErrorCodes or InventoryUiErrors code. Null if it would be accepted.</summary>
        public string Error { get; }

        /// <summary>A sentence for the player saying why it's refused, or null.</summary>
        public string Message { get; }

        /// <summary>Releasing here does something the server will accept.</summary>
        public bool IsValid => Error == null && Action != DropAction.None;

        /// <summary>For a grid drop: the container whose cells to highlight, and the cells the item would cover.</summary>
        public string GridId { get; private set; }

        public int X { get; private set; }

        public int Y { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>For a slot drop: the slot to highlight.</summary>
        public string SlotParentId { get; private set; }

        public string SlotName { get; private set; }

        /// <summary>For a merge, load or put-inside: the item to highlight.</summary>
        public string TargetItemId { get; private set; }

        public override string ToString() => IsValid ? Action + " (" + Ops.Count + " ops)" : Action + " refused: " + Error;

        internal static DropPlan Nothing(string error = null, string message = null) => new DropPlan(DropAction.None, null, error, message);

        internal static DropPlan Refused(DropAction action, string error, string message) => new DropPlan(action, null, error, message);

        internal static DropPlan For(DropAction action, IReadOnlyList<InventoryOp> ops, ApplyResult check)
        {
            return check == null || check.Success
                ? new DropPlan(action, ops, null, null)
                : new DropPlan(action, ops, check.Error.Code, InventoryUiErrors.Describe(check.Error.Code));
        }

        internal static DropPlan Dropping() => new DropPlan(DropAction.Drop, null, null, null);

        internal DropPlan OnGrid(string gridId, int x, int y, int width, int height)
        {
            GridId = gridId;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            return this;
        }

        internal DropPlan OnSlot(string parentId, string slot)
        {
            SlotParentId = parentId;
            SlotName = slot;
            return this;
        }

        internal DropPlan OnItem(string itemId)
        {
            TargetItemId = itemId;
            return this;
        }
    }
}
