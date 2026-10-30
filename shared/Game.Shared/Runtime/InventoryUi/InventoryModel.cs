using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>
    /// What the inventory screen works with: the source's containers, a tree of them to check drops against,
    /// and the plans for drags and quick moves. The screen asks it what a drop would do while dragging, and
    /// tells it to carry out the plan when the item is let go.
    /// </summary>
    public sealed class InventoryModel : IDisposable
    {
        private readonly ItemCatalog _catalog;
        private readonly IInventorySource _source;
        private readonly List<string> _opened = new List<string>();
        private InventoryTree _tree;

        public InventoryModel(ItemCatalog catalog, IInventorySource source)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _source.Changed += OnChanged;
            _source.Refused += OnRefused;
        }

        /// <summary>Something on screen changed. Redraw.</summary>
        public event Action Changed;

        /// <summary>The server refused a request: a sentence to show the player.</summary>
        public event Action<string> Refused;

        public ItemCatalog Catalog => _catalog;

        public IInventorySource Source => _source;

        /// <summary>The player's character, or null before the server has sent it.</summary>
        public InventoryRoot Character => _source.Roots.FirstOrDefault(r => r.Kind == InventoryRootKind.Character);

        /// <summary>Everything else on screen, in the source's order.</summary>
        public IEnumerable<InventoryRoot> Others => _source.Roots.Where(r => r.Kind != InventoryRootKind.Character);

        /// <summary>The visible containers as a tree, for checking drops. Rebuilt when the source changes.</summary>
        public InventoryTree Tree => _tree ?? (_tree = new InventoryTree(_catalog, _source.Roots.Select(r => r.Root.Clone())));

        public bool IsPending(string itemId) => _source.IsPending(itemId);

        /// <summary>
        /// Containers opened from inside others, newest first: a backpack lying in a crate, a pouch in your
        /// backpack. Only the screen sees these; their contents are already in the tree. Anything that's gone,
        /// or is now shown anyway (equipped, say), drops out of the list when the source changes.
        /// </summary>
        public IReadOnlyList<string> OpenedItems => _opened;

        /// <summary>
        /// Whether the item can be opened as its own panel: it has grids, and isn't already on screen as a root
        /// or as the gear of a character or body (a rig or backpack in its slot).
        /// </summary>
        public bool CanOpen(string itemId)
        {
            var tree = Tree;
            if (itemId == null || !tree.TryGet(itemId, out var item) || !_catalog.TryGet(item.DefId, out var def) || !def.HasGrids())
            {
                return false;
            }

            var parent = tree.ParentOf(itemId);
            if (parent == null)
            {
                return false;
            }

            var root = _source.Roots.FirstOrDefault(r => r.Root.Id == parent.Id);
            return root == null || (root.Kind != InventoryRootKind.Character && root.Kind != InventoryRootKind.Body);
        }

        /// <summary>Show a container inside another as its own panel (double-click). False if it can't be opened.</summary>
        public bool OpenItem(string itemId)
        {
            if (!CanOpen(itemId))
            {
                return false;
            }

            _opened.Remove(itemId);
            _opened.Insert(0, itemId);
            Changed?.Invoke();
            return true;
        }

        public void CloseItem(string itemId)
        {
            if (_opened.Remove(itemId))
            {
                Changed?.Invoke();
            }
        }

        public bool IsOpenedItem(string itemId) => itemId != null && _opened.Contains(itemId);

        /// <summary>Whether the item is a stack of more than one, so part of it can be split off.</summary>
        public bool CanSplit(string itemId) =>
            itemId != null && Tree.TryGet(itemId, out var item) && _catalog.TryGet(item.DefId, out var def) && def.MaxStack > 1 && item.Quantity > 1;

        /// <summary>What letting go of the item on the target would do, checked by the server's rules.</summary>
        /// <param name="quantity">For a shift-drag, how many to take off the stack; 0 for all of it.</param>
        public DropPlan Plan(string itemId, bool rotated, DropTarget target, int quantity = 0)
        {
            if (_source.IsPending(itemId))
            {
                return DropPlan.Refused(DropAction.None, InventoryUiErrors.Pending, InventoryUiErrors.Describe(InventoryUiErrors.Pending));
            }

            if (target != null && target.Kind == DropTargetKind.Outside && !_source.CanDrop)
            {
                return DropPlan.Refused(DropAction.Drop, InventoryUiErrors.CantDrop, InventoryUiErrors.Describe(InventoryUiErrors.CantDrop));
            }

            return quantity > 0
                ? DropResolver.ResolvePart(Tree, itemId, quantity, rotated, target)
                : DropResolver.Resolve(Tree, itemId, rotated, target);
        }

        /// <summary>Where Ctrl+click would send the item.</summary>
        public DropPlan PlanQuickMove(string itemId)
        {
            if (_source.IsPending(itemId))
            {
                return DropPlan.Refused(DropAction.None, InventoryUiErrors.Pending, InventoryUiErrors.Describe(InventoryUiErrors.Pending));
            }

            // Opened containers count as the other side too, ahead of the rest: a backpack you opened in a crate
            // is where Ctrl+click from your gear should go. Not ones in your own gear, which is this side.
            var characterId = Character?.Root.Id;
            var tree = Tree;
            var others = _opened.Where(id => tree.RootOf(id)?.Id != characterId).Concat(Others.Select(r => r.Root.Id)).ToList();
            return DropResolver.QuickMove(tree, itemId, characterId, others);
        }

        /// <summary>Carries out a plan. False, sending nothing, if it isn't valid.</summary>
        public bool Execute(string itemId, DropPlan plan)
        {
            if (plan == null || !plan.IsValid)
            {
                return false;
            }

            if (plan.Action == DropAction.Drop)
            {
                _source.Drop(itemId);
            }
            else
            {
                _source.Send(plan.Ops);
            }

            return true;
        }

        public void Close(string rootId) => _source.Close(rootId);

        public void Dispose()
        {
            _source.Changed -= OnChanged;
            _source.Refused -= OnRefused;
        }

        private void OnChanged()
        {
            _tree = null;
            _opened.RemoveAll(id => !CanOpen(id));
            Changed?.Invoke();
        }

        private void OnRefused(string code) => Refused?.Invoke(InventoryUiErrors.Describe(code));
    }
}
