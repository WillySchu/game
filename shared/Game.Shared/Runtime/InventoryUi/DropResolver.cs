using System.Collections.Generic;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>
    /// Turns "the player let go of this item here" into ops, and checks them against the tree with exactly the
    /// rules the server applies (<see cref="InventoryTree.Check"/>), so the screen can show green or red while
    /// dragging and never sends something it knows will be refused.
    /// </summary>
    public static class DropResolver
    {
        /// <summary>What dropping the item on the target would do.</summary>
        /// <param name="rotated">Whether the dragged item is rotated now (R while dragging), which only matters on a grid.</param>
        public static DropPlan Resolve(InventoryTree tree, string itemId, bool rotated, DropTarget target)
        {
            if (tree == null || itemId == null || target == null || !tree.TryGet(itemId, out var item)
                || !tree.Catalog.TryGet(item.DefId, out var def) || tree.ParentOf(itemId) == null)
            {
                return DropPlan.Nothing();
            }

            switch (target.Kind)
            {
                case DropTargetKind.GridCell:
                    return OnGridCell(tree, item, def, rotated, target);
                case DropTargetKind.Slot:
                    return OnSlot(tree, item, target);
                case DropTargetKind.Item:
                    return OnItem(tree, item, def, target.ItemId);
                case DropTargetKind.Outside:
                    return DropPlan.Dropping();
                default:
                    return DropPlan.Nothing();
            }
        }

        /// <summary>
        /// Ctrl+click: send the item to the most sensible place on the other side. From the character, into the
        /// first open container with room. From anywhere else, onto the character: an empty slot that takes
        /// it (a rifle into the primary slot), then the rig, then the backpack.
        /// </summary>
        /// <param name="others">The other containers on screen, in the order to try them (the one opened last first, say).</param>
        public static DropPlan QuickMove(InventoryTree tree, string itemId, string characterId, IReadOnlyList<string> others)
        {
            if (tree == null || itemId == null || characterId == null || !tree.TryGet(itemId, out var item)
                || !tree.Catalog.TryGet(item.DefId, out var def) || tree.ParentOf(itemId) == null
                || !tree.TryGet(characterId, out var character))
            {
                return DropPlan.Nothing();
            }

            var candidates = new List<(InventoryTarget To, string Highlight, bool IsSlot)>();
            if (tree.RootOf(itemId) == character)
            {
                foreach (var otherId in others ?? new string[0])
                {
                    if (otherId != characterId && tree.TryGet(otherId, out var other) && HasGrid(tree.Catalog, other))
                    {
                        candidates.Add((InventoryTarget.AnyGridSpot(other.Id), other.Id, false));
                    }
                }
            }
            else
            {
                if (tree.Catalog.TryGet(character.DefId, out var characterDef))
                {
                    foreach (var spec in SlotsOf(characterDef))
                    {
                        if (spec.Name != SlotNames.Grid && InventoryValidator.Accepts(spec, def) && SlotOccupant(character, spec.Name) == null)
                        {
                            candidates.Add((InventoryTarget.NamedSlot(character.Id, spec.Name), character.Id + "/" + spec.Name, true));
                        }
                    }
                }

                foreach (var slot in new[] { SlotNames.Rig, SlotNames.Backpack })
                {
                    var container = SlotOccupant(character, slot);
                    if (container != null && HasGrid(tree.Catalog, container))
                    {
                        candidates.Add((InventoryTarget.AnyGridSpot(container.Id), container.Id, false));
                    }
                }
            }

            foreach (var (to, highlight, isSlot) in candidates)
            {
                var ops = new[] { InventoryOp.Move(item.Id, to) };
                var check = tree.Check(ops);
                if (check.Success)
                {
                    var plan = DropPlan.For(DropAction.Move, ops, check);
                    return isSlot ? plan.OnSlot(to.ParentId, to.Slot) : plan.OnItem(highlight);
                }
            }

            return DropPlan.Refused(DropAction.Move, InventoryUiErrors.NowhereToGo, InventoryUiErrors.Describe(InventoryUiErrors.NowhereToGo));
        }

        private static DropPlan OnGridCell(InventoryTree tree, ItemNode item, ItemDefinition def, bool rotated, DropTarget target)
        {
            GridPlacement.GetSize(def, rotated, out int width, out int height);
            if (tree.ParentOf(item.Id)?.Id == target.ParentId && item.Slot == SlotNames.Grid
                && item.X == target.X && item.Y == target.Y && item.Rotated == rotated)
            {
                // Back where it started: nothing to send.
                return DropPlan.Nothing().OnGrid(target.ParentId, target.X, target.Y, width, height);
            }

            var ops = new[] { InventoryOp.Move(item.Id, InventoryTarget.Grid(target.ParentId, target.X, target.Y, rotated)) };
            return DropPlan.For(DropAction.Move, ops, tree.Check(ops)).OnGrid(target.ParentId, target.X, target.Y, width, height);
        }

        private static DropPlan OnSlot(InventoryTree tree, ItemNode item, DropTarget target)
        {
            if (!tree.TryGet(target.ParentId, out var parent))
            {
                return DropPlan.Nothing();
            }

            // A filled slot shows its item, so dropping there means dropping on that item.
            var occupant = SlotOccupant(parent, target.Slot);
            if (occupant != null)
            {
                return occupant.Id == item.Id ? DropPlan.Nothing().OnSlot(parent.Id, target.Slot) : Resolve(tree, item.Id, false, DropTarget.OnItem(occupant.Id));
            }

            var ops = new List<InventoryOp> { IntoSlot(tree, item, parent, target.Slot) };
            return DropPlan.For(DropAction.Move, ops, tree.Check(ops)).OnSlot(parent.Id, target.Slot);
        }

        private static DropPlan OnItem(InventoryTree tree, ItemNode item, ItemDefinition def, string targetId)
        {
            if (targetId == item.Id || !tree.TryGet(targetId, out var target) || !tree.Catalog.TryGet(target.DefId, out var targetDef))
            {
                return DropPlan.Nothing();
            }

            // Onto something inside itself (a backpack onto an item it holds): the check would say cycle anyway.
            if (IsInside(tree, target, item.Id))
            {
                return DropPlan.Refused(DropAction.Move, InventoryErrorCodes.Cycle, InventoryUiErrors.Describe(InventoryErrorCodes.Cycle)).OnItem(target.Id);
            }

            // The same stackable item: top it up.
            if (target.DefId == item.DefId && def.MaxStack > 1)
            {
                var merge = new[] { InventoryOp.Merge(item.Id, target.Id) };
                return DropPlan.For(DropAction.Merge, merge, tree.Check(merge)).OnItem(target.Id);
            }

            // A slot of the target that takes it: load it.
            DropPlan refusedLoad = null;
            foreach (var spec in SlotsOf(targetDef))
            {
                if (spec.Name == SlotNames.Grid || !InventoryValidator.Accepts(spec, def))
                {
                    continue;
                }

                var occupant = SlotOccupant(target, spec.Name);
                InventoryOp op;
                if (occupant == null)
                {
                    op = IntoSlot(tree, item, target, spec.Name);
                }
                else if (occupant.DefId == item.DefId && def.MaxStack > 1)
                {
                    op = InventoryOp.Merge(item.Id, occupant.Id);
                }
                else
                {
                    refusedLoad = refusedLoad ?? DropPlan.Refused(DropAction.Load, InventoryErrorCodes.SlotOccupied, InventoryUiErrors.Describe(InventoryErrorCodes.SlotOccupied)).OnItem(target.Id);
                    continue;
                }

                var ops = new[] { op };
                var plan = DropPlan.For(DropAction.Load, ops, tree.Check(ops)).OnItem(target.Id);
                if (plan.IsValid)
                {
                    return plan;
                }

                refusedLoad = refusedLoad ?? plan;
            }

            if (refusedLoad != null)
            {
                return refusedLoad;
            }

            // A container: put it inside, wherever there's room.
            if (targetDef.Grid != null)
            {
                var put = new[] { InventoryOp.Move(item.Id, InventoryTarget.AnyGridSpot(target.Id)) };
                return DropPlan.For(DropAction.PutInside, put, tree.Check(put)).OnItem(target.Id);
            }

            return DropPlan.Refused(DropAction.Move, InventoryUiErrors.CantCombine, InventoryUiErrors.Describe(InventoryUiErrors.CantCombine)).OnItem(target.Id);
        }

        // Into an empty named slot. A stack bigger than the slot holds (60 rounds into a 30-round magazine) is
        // split, so the slot gets what it can and the rest stays where it was.
        private static InventoryOp IntoSlot(InventoryTree tree, ItemNode item, ItemNode parent, string slot)
        {
            var to = InventoryTarget.NamedSlot(parent.Id, slot);
            if (tree.Catalog.TryGet(parent.DefId, out var parentDef))
            {
                var spec = SlotsOf(parentDef).Find(s => s.Name == slot);
                if (spec != null && spec.MaxQuantity > 0 && item.Quantity > spec.MaxQuantity)
                {
                    return InventoryOp.Split(item.Id, spec.MaxQuantity, to);
                }
            }

            return InventoryOp.Move(item.Id, to);
        }

        private static List<SlotSpec> SlotsOf(ItemDefinition def) => def.Slots ?? new List<SlotSpec>();

        private static ItemNode SlotOccupant(ItemNode parent, string slot)
        {
            foreach (var child in parent.Children)
            {
                if (child.Slot == slot)
                {
                    return child;
                }
            }

            return null;
        }

        private static bool HasGrid(ItemCatalog catalog, ItemNode node) => catalog.TryGet(node.DefId, out var def) && def.Grid != null;

        private static bool IsInside(InventoryTree tree, ItemNode node, string ancestorId)
        {
            for (var current = tree.ParentOf(node.Id); current != null; current = tree.ParentOf(current.Id))
            {
                if (current.Id == ancestorId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
