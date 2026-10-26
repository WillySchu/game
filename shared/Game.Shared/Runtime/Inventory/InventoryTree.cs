using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;

namespace RaidZero.Shared.Inventory
{
    /// <summary>
    /// A set of root containers and every item under them, plus the operations that rearrange them.
    ///
    /// The roots decide what an operation can reach. The backend builds a tree from one player's stash
    /// and character, so a client can never touch anyone else's items: any other ID is simply
    /// "item_not_found". The game server builds one tree per raid holding every player's character, every
    /// crate and every body, so picking up, dropping and looting are all just moves between roots.
    ///
    /// <see cref="Apply(IReadOnlyList{InventoryOp})"/> is all-or-nothing: the ops run against a copy, the
    /// result is fully validated, and only then does it replace the tree. A successful apply replaces the
    /// node objects, so hold on to item IDs, not to <see cref="ItemNode"/> references.
    /// </summary>
    public sealed class InventoryTree
    {
        private readonly ItemCatalog _catalog;
        private readonly Func<string> _newId;
        private List<ItemNode> _roots;
        private Dictionary<string, ItemNode> _byId;
        private Dictionary<string, ItemNode> _parentOf;

        /// <param name="catalog">The item rules.</param>
        /// <param name="roots">Root containers. The tree takes ownership; don't modify them afterwards.</param>
        /// <param name="newId">Mints IDs for items created by splits. Defaults to <see cref="Uuid7.NewId"/>.</param>
        public InventoryTree(ItemCatalog catalog, IEnumerable<ItemNode> roots, Func<string> newId = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _newId = newId ?? Uuid7.NewId;
            _roots = (roots ?? throw new ArgumentNullException(nameof(roots))).ToList();
            BuildIndex(_roots, out _byId, out _parentOf);
        }

        public ItemCatalog Catalog => _catalog;

        public IReadOnlyList<ItemNode> Roots => _roots;

        public bool TryGet(string itemId, out ItemNode node)
        {
            if (itemId == null)
            {
                node = null;
                return false;
            }

            return _byId.TryGetValue(itemId, out node);
        }

        /// <summary>The container an item sits in; null for roots and unknown IDs.</summary>
        public ItemNode ParentOf(string itemId)
        {
            return itemId != null && _parentOf.TryGetValue(itemId, out var parent) ? parent : null;
        }

        public ApplyResult Apply(InventoryOp op)
        {
            return Apply(new[] { op });
        }

        /// <summary>Runs the ops in order. If any op is rejected, or the result is invalid, nothing changes.</summary>
        public ApplyResult Apply(IReadOnlyList<InventoryOp> ops)
        {
            if (ops == null)
            {
                throw new ArgumentNullException(nameof(ops));
            }

            var work = new Workspace(this);
            for (int i = 0; i < ops.Count; i++)
            {
                var error = work.ApplyOne(i, ops[i]);
                if (error != null)
                {
                    return ApplyResult.Failed(i, error);
                }
            }

            // Only roots the batch touched can have become invalid; on a raid server that is one or two
            // containers out of hundreds.
            foreach (var root in work.TouchedRoots)
            {
                var errors = InventoryValidator.Validate(_catalog, root);
                if (errors.Count > 0)
                {
                    return ApplyResult.Failed(null, errors[0]);
                }
            }

            _roots = work.Roots;
            _byId = work.ById;
            _parentOf = work.ParentOf;

            var created = work.Created.Where(c => _byId.ContainsKey(c.ItemId)).ToList();
            return ApplyResult.Succeeded(created, work.Changes);
        }

        private static void BuildIndex(List<ItemNode> roots, out Dictionary<string, ItemNode> byId, out Dictionary<string, ItemNode> parentOf)
        {
            byId = new Dictionary<string, ItemNode>(StringComparer.Ordinal);
            parentOf = new Dictionary<string, ItemNode>(StringComparer.Ordinal);

            var stack = new Stack<(ItemNode Node, ItemNode Parent)>();
            foreach (var root in roots)
            {
                stack.Push((root ?? throw new ArgumentException("A root is null.", nameof(roots)), null));
            }

            while (stack.Count > 0)
            {
                var (node, parent) = stack.Pop();
                if (node.Id == null || byId.ContainsKey(node.Id))
                {
                    throw new ArgumentException("Every item needs a unique ID; found '" + node.Id + "' more than once or missing.", nameof(roots));
                }

                byId.Add(node.Id, node);
                if (parent != null)
                {
                    parentOf.Add(node.Id, parent);
                }

                if (node.Children == null)
                {
                    node.Children = new List<ItemNode>();
                }

                foreach (var child in node.Children)
                {
                    stack.Push((child ?? throw new ArgumentException("Item '" + node.Id + "' has a null child.", nameof(roots)), node));
                }
            }
        }

        /// <summary>A private copy of the tree that one batch of ops mutates.</summary>
        private sealed class Workspace
        {
            private readonly ItemCatalog _catalog;
            private readonly Func<string> _newId;

            public Workspace(InventoryTree tree)
            {
                _catalog = tree._catalog;
                _newId = tree._newId;
                Roots = tree._roots.Select(r => r.Clone()).ToList();
                BuildIndex(Roots, out var byId, out var parentOf);
                ById = byId;
                ParentOf = parentOf;
            }

            public List<ItemNode> Roots { get; }

            public Dictionary<string, ItemNode> ById { get; }

            public Dictionary<string, ItemNode> ParentOf { get; }

            public List<CreatedItem> Created { get; } = new List<CreatedItem>();

            public ChangeSet Changes { get; } = new ChangeSet();

            public HashSet<ItemNode> TouchedRoots { get; } = new HashSet<ItemNode>();

            public InventoryError ApplyOne(int index, InventoryOp op)
            {
                switch (op?.Op)
                {
                    case InventoryOpKinds.Move: return Move(op);
                    case InventoryOpKinds.Split: return Split(index, op);
                    case InventoryOpKinds.Merge: return Merge(op);
                    case InventoryOpKinds.Discard: return Discard(op);
                    default:
                        return new InventoryError(op?.ItemId, InventoryErrorCodes.UnknownOp, "Unknown op '" + op?.Op + "'.");
                }
            }

            private InventoryError Move(InventoryOp op)
            {
                var error = FindMovable(op.ItemId, out var item, out var def);
                if (error != null)
                {
                    return error;
                }

                error = FindTarget(item.Id, op.To, out var parent);
                if (error != null)
                {
                    return error;
                }

                if (IsSelfOrDescendant(parent, item.Id))
                {
                    return new InventoryError(item.Id, InventoryErrorCodes.Cycle, "An item cannot go inside itself.");
                }

                error = ResolvePlacement(item.Id, def, item.Quantity, parent, op.To, out var placement);
                if (error != null)
                {
                    return error;
                }

                Touch(item);
                Detach(item);
                Attach(item, parent, placement);
                Touch(parent);
                Changes.MarkUpdated(item.Id);
                return null;
            }

            private InventoryError Split(int index, InventoryOp op)
            {
                var error = FindMovable(op.ItemId, out var item, out var def);
                if (error != null)
                {
                    return error;
                }

                if (def.MaxStack < 2)
                {
                    return new InventoryError(item.Id, InventoryErrorCodes.NotStackable, def.DefId + " doesn't stack.");
                }

                if (op.Quantity < 1 || op.Quantity >= item.Quantity)
                {
                    return new InventoryError(item.Id, InventoryErrorCodes.InvalidQuantity,
                        "Split quantity must be between 1 and " + (item.Quantity - 1) + ", got " + op.Quantity + ".");
                }

                error = FindTarget(null, op.To, out var parent);
                if (error != null)
                {
                    return error;
                }

                error = ResolvePlacement(null, def, op.Quantity, parent, op.To, out var placement);
                if (error != null)
                {
                    return error;
                }

                var id = _newId();
                if (id == null || ById.ContainsKey(id))
                {
                    throw new InvalidOperationException("The ID generator returned a missing or duplicate ID '" + id + "'.");
                }

                var piece = new ItemNode { Id = id, DefId = item.DefId, Quantity = op.Quantity };
                item.Quantity -= op.Quantity;
                ById.Add(id, piece);
                Attach(piece, parent, placement);
                Touch(item);
                Touch(parent);

                Created.Add(new CreatedItem(index, id));
                Changes.MarkCreated(id);
                Changes.MarkUpdated(item.Id);
                return null;
            }

            private InventoryError Merge(InventoryOp op)
            {
                var error = FindMovable(op.ItemId, out var source, out var def);
                if (error != null)
                {
                    return error;
                }

                if (op.IntoId == null || !ById.TryGetValue(op.IntoId, out var target))
                {
                    return new InventoryError(op.IntoId, InventoryErrorCodes.TargetNotFound, "No item '" + op.IntoId + "' to merge into.");
                }

                if (target == source)
                {
                    return new InventoryError(source.Id, InventoryErrorCodes.StackMismatch, "An item cannot merge into itself.");
                }

                if (target.DefId != source.DefId)
                {
                    return new InventoryError(source.Id, InventoryErrorCodes.StackMismatch, "Only stacks of the same item merge.");
                }

                if (def.MaxStack < 2)
                {
                    return new InventoryError(source.Id, InventoryErrorCodes.NotStackable, def.DefId + " doesn't stack.");
                }

                if (op.Quantity < 0 || op.Quantity > source.Quantity)
                {
                    return new InventoryError(source.Id, InventoryErrorCodes.InvalidQuantity,
                        "Merge quantity must be between 0 (all that fit) and " + source.Quantity + ", got " + op.Quantity + ".");
                }

                int room = StackLimit(target, def) - target.Quantity;
                if (room <= 0)
                {
                    return new InventoryError(target.Id, InventoryErrorCodes.StackFull, "That stack is full.");
                }

                Touch(source);
                Touch(target);
                int wanted = op.Quantity == 0 ? source.Quantity : op.Quantity;
                int moved = Math.Min(room, wanted);
                target.Quantity += moved;
                source.Quantity -= moved;
                Changes.MarkUpdated(target.Id);

                if (source.Quantity == 0)
                {
                    Remove(source, RemovalReason.Merged);
                }
                else
                {
                    Changes.MarkUpdated(source.Id);
                }

                return null;
            }

            private InventoryError Discard(InventoryOp op)
            {
                var error = FindMovable(op.ItemId, out var item, out _);
                if (error != null)
                {
                    return error;
                }

                Touch(item);
                Remove(item, RemovalReason.Discarded);
                return null;
            }

            // An item that exists, isn't a root, and has a known definition.
            private InventoryError FindMovable(string itemId, out ItemNode item, out ItemDefinition def)
            {
                def = null;
                if (itemId == null || !ById.TryGetValue(itemId, out item))
                {
                    item = null;
                    return new InventoryError(itemId, InventoryErrorCodes.ItemNotFound, "No item '" + itemId + "'.");
                }

                if (!ParentOf.ContainsKey(item.Id))
                {
                    return new InventoryError(item.Id, InventoryErrorCodes.RootItem, "Root containers can't be moved, split or discarded.");
                }

                if (!_catalog.TryGet(item.DefId, out def))
                {
                    return new InventoryError(item.Id, InventoryErrorCodes.UnknownDef, "Unknown def_id '" + item.DefId + "'.");
                }

                return null;
            }

            private InventoryError FindTarget(string itemId, InventoryTarget to, out ItemNode parent)
            {
                parent = null;
                if (to == null || to.ParentId == null || !ById.TryGetValue(to.ParentId, out parent))
                {
                    return new InventoryError(itemId, InventoryErrorCodes.TargetNotFound, "No container '" + to?.ParentId + "'.");
                }

                return null;
            }

            private bool IsSelfOrDescendant(ItemNode node, string ancestorId)
            {
                for (var current = node; current != null; current = ParentOf.TryGetValue(current.Id, out var up) ? up : null)
                {
                    if (current.Id == ancestorId)
                    {
                        return true;
                    }
                }

                return false;
            }

            // Checks that an item of this definition and quantity can sit at the target, and works out
            // the exact cell when the target leaves it open. itemId is the item being moved, or null for
            // a new stack, so a move doesn't collide with the space the item already occupies.
            private InventoryError ResolvePlacement(string itemId, ItemDefinition def, int quantity, ItemNode parent, InventoryTarget to, out Placement placement)
            {
                placement = default;
                if (!_catalog.TryGet(parent.DefId, out var parentDef))
                {
                    return new InventoryError(parent.Id, InventoryErrorCodes.UnknownDef, "Unknown def_id '" + parent.DefId + "'.");
                }

                if (to.Slot == SlotNames.Grid)
                {
                    return ResolveGridPlacement(itemId, def, parent, parentDef, to, out placement);
                }

                if (to.X != null || to.Y != null || to.Rotated)
                {
                    return new InventoryError(itemId, InventoryErrorCodes.PositionMismatch, "Named slots take no x, y or rotation.");
                }

                var spec = to.Slot == null ? null : parentDef.Slots?.Find(s => s.Name == to.Slot);
                if (spec == null)
                {
                    return new InventoryError(itemId, InventoryErrorCodes.SlotMismatch, parentDef.DefId + " has no slot '" + to.Slot + "'.");
                }

                if (!InventoryValidator.Accepts(spec, def))
                {
                    return new InventoryError(itemId, InventoryErrorCodes.SlotMismatch, "Slot '" + spec.Name + "' does not accept " + def.DefId + ".");
                }

                if (parent.Children.Any(c => c.Slot == spec.Name && c.Id != itemId))
                {
                    return new InventoryError(itemId, InventoryErrorCodes.SlotOccupied, "Slot '" + spec.Name + "' already holds an item.");
                }

                if (spec.MaxQuantity > 0 && quantity > spec.MaxQuantity)
                {
                    return new InventoryError(itemId, InventoryErrorCodes.QuantityOutOfRange,
                        "Slot '" + spec.Name + "' holds at most " + spec.MaxQuantity + ", got " + quantity + ".");
                }

                placement = new Placement(spec.Name, null, null, false);
                return null;
            }

            private InventoryError ResolveGridPlacement(string itemId, ItemDefinition def, ItemNode parent, ItemDefinition parentDef, InventoryTarget to, out Placement placement)
            {
                placement = default;
                if (parentDef.Grid == null)
                {
                    return new InventoryError(itemId, InventoryErrorCodes.SlotMismatch, parentDef.DefId + " has no grid.");
                }

                if ((to.X == null) != (to.Y == null))
                {
                    return new InventoryError(itemId, InventoryErrorCodes.PositionMismatch, "Give both x and y, or neither for the first free spot.");
                }

                if (to.X == null)
                {
                    if (!GridPlacement.TryFindFreeSpot(_catalog, parent, def, out int fx, out int fy, out bool frot))
                    {
                        return new InventoryError(itemId, InventoryErrorCodes.NoSpace, "No room for " + def.DefId + " in " + parentDef.DefId + ".");
                    }

                    placement = new Placement(SlotNames.Grid, fx, fy, frot);
                    return null;
                }

                int x = to.X.Value;
                int y = to.Y.Value;
                GridPlacement.GetSize(def, to.Rotated, out int w, out int h);
                if (x < 0 || y < 0 || x + w > parentDef.Grid.Width || y + h > parentDef.Grid.Height)
                {
                    return new InventoryError(itemId, InventoryErrorCodes.OutOfBounds,
                        def.DefId + " at (" + x + "," + y + ") does not fit in a " + parentDef.Grid.Width + "x" + parentDef.Grid.Height + " grid.");
                }

                if (!GridPlacement.Fits(_catalog, parent, def, x, y, to.Rotated, itemId))
                {
                    return new InventoryError(itemId, InventoryErrorCodes.Overlap, "That space is taken.");
                }

                placement = new Placement(SlotNames.Grid, x, y, to.Rotated);
                return null;
            }

            // The most a stack can hold where it is: its max stack, or less in a capped slot like a magazine.
            private int StackLimit(ItemNode stack, ItemDefinition def)
            {
                int limit = def.MaxStack;
                if (stack.Slot != null && stack.Slot != SlotNames.Grid
                    && ParentOf.TryGetValue(stack.Id, out var parent)
                    && _catalog.TryGet(parent.DefId, out var parentDef))
                {
                    var spec = parentDef.Slots?.Find(s => s.Name == stack.Slot);
                    if (spec != null && spec.MaxQuantity > 0)
                    {
                        limit = Math.Min(limit, spec.MaxQuantity);
                    }
                }

                return limit;
            }

            private void Touch(ItemNode node)
            {
                var current = node;
                while (ParentOf.TryGetValue(current.Id, out var up))
                {
                    current = up;
                }

                TouchedRoots.Add(current);
            }

            private void Detach(ItemNode item)
            {
                if (ParentOf.TryGetValue(item.Id, out var parent))
                {
                    parent.Children.Remove(item);
                    ParentOf.Remove(item.Id);
                }
            }

            private void Attach(ItemNode item, ItemNode parent, Placement placement)
            {
                item.Slot = placement.Slot;
                item.X = placement.X;
                item.Y = placement.Y;
                item.Rotated = placement.Rotated;
                parent.Children.Add(item);
                ParentOf[item.Id] = parent;
            }

            private void Remove(ItemNode item, RemovalReason reason)
            {
                Detach(item);
                foreach (var node in item.Walk())
                {
                    ById.Remove(node.Id);
                    ParentOf.Remove(node.Id);
                    Changes.MarkRemoved(node.Id, reason);
                }
            }
        }

        private struct Placement
        {
            public Placement(string slot, int? x, int? y, bool rotated)
            {
                Slot = slot;
                X = x;
                Y = y;
                Rotated = rotated;
            }

            public string Slot { get; }

            public int? X { get; }

            public int? Y { get; }

            public bool Rotated { get; }
        }
    }
}
