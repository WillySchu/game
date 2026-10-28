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

        /// <summary>What letting go of the item on the target would do, checked by the server's rules.</summary>
        public DropPlan Plan(string itemId, bool rotated, DropTarget target)
        {
            if (_source.IsPending(itemId))
            {
                return DropPlan.Refused(DropAction.None, InventoryUiErrors.Pending, InventoryUiErrors.Describe(InventoryUiErrors.Pending));
            }

            if (target != null && target.Kind == DropTargetKind.Outside && !_source.CanDrop)
            {
                return DropPlan.Refused(DropAction.Drop, InventoryUiErrors.CantDrop, InventoryUiErrors.Describe(InventoryUiErrors.CantDrop));
            }

            return DropResolver.Resolve(Tree, itemId, rotated, target);
        }

        /// <summary>Where Ctrl+click would send the item.</summary>
        public DropPlan PlanQuickMove(string itemId)
        {
            if (_source.IsPending(itemId))
            {
                return DropPlan.Refused(DropAction.None, InventoryUiErrors.Pending, InventoryUiErrors.Describe(InventoryUiErrors.Pending));
            }

            var character = Character;
            return DropResolver.QuickMove(Tree, itemId, character?.Root.Id, Others.Select(r => r.Root.Id).ToList());
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
            Changed?.Invoke();
        }

        private void OnRefused(string code) => Refused?.Invoke(InventoryUiErrors.Describe(code));
    }
}
