using System;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// Dragging items around the inventory screen. Press on an item and a ghost of it follows the pointer;
    /// whatever's under the pointer is turned into a DropTarget and checked with InventoryModel.Plan, the
    /// same rules the server applies, so the ghost and the spot it would land on go green or red as you move.
    /// Letting go sends the plan if it's good. If it isn't, nothing is sent: the item never left, so it simply
    /// stays put, and the reason goes to the status line. Ctrl+click (Cmd+click on a Mac) quick-moves instead.
    /// </summary>
    internal sealed class InventoryDrag
    {
        private readonly VisualElement _root;
        private readonly VisualElement _window;
        private readonly InventoryPanels _panels;
        private readonly VisualElement _ghost;
        private readonly Label _ghostName;
        private readonly VisualElement _cellHighlight;

        private InventoryModel _model;
        private string _itemId;
        private int _pointerId = -1;
        private bool _rotated;
        private int _width;
        private int _height;
        private float _grabX;
        private float _grabY;
        private Vector2 _pointer;
        private VisualElement _highlighted;

        public InventoryDrag(VisualElement root, VisualElement window, InventoryPanels panels)
        {
            _root = root;
            _window = window;
            _panels = panels;

            _ghost = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("inv-ghost");
            _ghostName = new Label { pickingMode = PickingMode.Ignore }.WithClass("inv-item__name");
            _ghost.Add(_ghostName);
            _ghost.style.position = Position.Absolute;
            _ghost.style.display = DisplayStyle.None;
            _root.Add(_ghost);

            _cellHighlight = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("inv-drop-cells");

            _panels.ItemPressed += OnItemPressed;
            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _root.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _root.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
        }

        /// <summary>A sentence for the status line: why a drop or quick move was refused.</summary>
        public event Action<string> Message;

        public bool IsDragging => _itemId != null;

        public string ItemId => _itemId;

        public void SetModel(InventoryModel model)
        {
            Cancel();
            _model = model;
        }

        /// <summary>R while dragging: turn the item a quarter, keeping the grab point on the same part of it.</summary>
        public void Rotate()
        {
            if (!IsDragging)
            {
                return;
            }

            DragGeometry.RotateGrab(_grabX, _grabY, _width * InventoryPanels.Cell, _height * InventoryPanels.Cell, out _grabX, out _grabY);
            var w = _width;
            _width = _height;
            _height = w;
            _rotated = !_rotated;
            SizeGhost();
            Track(_pointer);
        }

        /// <summary>Stop dragging without sending anything (Escape, the screen closing, the item going away).</summary>
        public void Cancel() => End(drop: false);

        /// <summary>
        /// After the panels were rebuilt (the server changed something mid-drag): mark the new copy of the
        /// dragged item, and check the plan again against the new state. If the item is gone, stop.
        /// </summary>
        public void Refresh()
        {
            if (!IsDragging)
            {
                return;
            }

            if (_model == null || !_model.Tree.TryGet(_itemId, out _))
            {
                Cancel();
                return;
            }

            _panels.FindItem(_itemId)?.AddToClassList("inv-item--dragging");
            _highlighted = null;
            Track(_pointer);
        }

        private void OnItemPressed(PointerDownEvent evt, VisualElement element)
        {
            if (_model == null || IsDragging || evt.button != 0 || !(element.userData is ItemRef item))
            {
                return;
            }

            evt.StopPropagation();
            if (_model.IsPending(item.ItemId))
            {
                Message?.Invoke(InventoryUiErrors.Describe(InventoryUiErrors.Pending));
                return;
            }

            if (evt.ctrlKey || evt.commandKey)
            {
                var quick = _model.PlanQuickMove(item.ItemId);
                if (!_model.Execute(item.ItemId, quick) && quick.Message != null)
                {
                    Message?.Invoke(quick.Message);
                }

                return;
            }

            if (!_model.Tree.TryGet(item.ItemId, out var node) || !_model.Catalog.TryGet(node.DefId, out var def))
            {
                return;
            }

            _itemId = item.ItemId;
            _pointerId = evt.pointerId;
            _rotated = node.Rotated;
            GridPlacement.GetSize(def, _rotated, out _width, out _height);

            // Where on the item it was grabbed, as a point on its full-size footprint. Items in slot boxes are
            // drawn to fit the box, so scale from the element's size rather than assuming it's footprint-sized.
            var local = element.WorldToLocal(evt.position);
            var size = element.layout;
            _grabX = Mathf.Clamp01(size.width > 0 ? local.x / size.width : 0) * _width * InventoryPanels.Cell;
            _grabY = Mathf.Clamp01(size.height > 0 ? local.y / size.height : 0) * _height * InventoryPanels.Cell;

            _ghostName.text = def.DisplayName ?? def.DefId;
            _ghost.ClearClassList();
            _ghost.AddToClassList("inv-ghost");
            _ghost.AddToClassList("inv-item--" + ItemBadges.Category(def));
            SizeGhost();
            _ghost.style.display = DisplayStyle.Flex;
            _ghost.BringToFront();
            element.AddToClassList("inv-item--dragging");

            _root.CapturePointer(_pointerId);
            Track(evt.position);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (IsDragging && evt.pointerId == _pointerId)
            {
                Track(evt.position);
            }
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (IsDragging && evt.pointerId == _pointerId)
            {
                _pointer = evt.position;
                End(drop: true);
            }
        }

        // Losing the pointer some other way (the window losing focus, say) is a cancel, not a drop.
        private void OnCaptureOut(PointerCaptureOutEvent evt)
        {
            if (IsDragging)
            {
                Cancel();
            }
        }

        private void Track(Vector2 position)
        {
            _pointer = position;
            var local = _root.WorldToLocal(position);
            _ghost.style.left = local.x - _grabX;
            _ghost.style.top = local.y - _grabY;

            if (_model == null)
            {
                return;
            }

            var target = TargetAt(position);
            Highlight(_model.Plan(_itemId, _rotated, target), target);
        }

        // What's under the pointer, as the drop rules see it. The ghost ignores picking, so this finds what's
        // beneath it; the dragged item's own element is skipped so moving it a cell or two lands on its grid.
        private DropTarget TargetAt(Vector2 position)
        {
            var picked = _root.panel?.Pick(position);
            for (var e = picked; e != null; e = e.parent)
            {
                switch (e.userData)
                {
                    case ItemRef item when item.ItemId != _itemId:
                        return DropTarget.OnItem(item.ItemId);
                    case SlotRef slot:
                        return DropTarget.NamedSlot(slot.ParentId, slot.SlotName);
                    case GridRef grid:
                        var inGrid = e.WorldToLocal(position);
                        DragGeometry.AnchorCell(inGrid.x, inGrid.y, _grabX, _grabY, InventoryPanels.Cell, out int x, out int y);
                        return DropTarget.GridCell(grid.ContainerId, grid.GridName, x, y);
                }

                if (e == _window)
                {
                    return DropTarget.None; // over the window, but not over anything to drop on
                }

                if (e == _root)
                {
                    break;
                }
            }

            return DropTarget.Outside;
        }

        private void Highlight(DropPlan plan, DropTarget target)
        {
            ClearHighlight();

            bool good = plan.IsValid;
            bool bad = !good && plan.Error != null;
            _ghost.EnableInClassList("inv-ghost--ok", good);
            _ghost.EnableInClassList("inv-ghost--bad", bad);
            _ghost.EnableInClassList("inv-ghost--drop", target.Kind == DropTargetKind.Outside && good);

            if (plan.GridId != null)
            {
                var grid = _panels.FindGrid(plan.GridId, plan.GridName);
                if (grid != null)
                {
                    InventoryPanels.Place(_cellHighlight, plan.X, plan.Y, plan.Width, plan.Height);
                    _cellHighlight.EnableInClassList("inv-drop-cells--ok", good);
                    _cellHighlight.EnableInClassList("inv-drop-cells--bad", bad);
                    grid.Add(_cellHighlight);
                }

                return;
            }

            _highlighted = plan.SlotParentId != null
                ? _panels.FindSlot(plan.SlotParentId, plan.SlotName)
                : _panels.FindItem(plan.TargetItemId);
            if (_highlighted != null)
            {
                _highlighted.EnableInClassList("inv-target--ok", good);
                _highlighted.EnableInClassList("inv-target--bad", bad);
            }
        }

        private void ClearHighlight()
        {
            _cellHighlight.RemoveFromHierarchy();
            if (_highlighted != null)
            {
                _highlighted.RemoveFromClassList("inv-target--ok");
                _highlighted.RemoveFromClassList("inv-target--bad");
                _highlighted = null;
            }
        }

        private void SizeGhost()
        {
            _ghost.style.width = _width * InventoryPanels.Cell;
            _ghost.style.height = _height * InventoryPanels.Cell;
        }

        private void End(bool drop)
        {
            if (!IsDragging)
            {
                return;
            }

            var itemId = _itemId;
            var plan = drop && _model != null ? _model.Plan(itemId, _rotated, TargetAt(_pointer)) : null;
            _itemId = null; // first, so the capture-out event that releasing raises is ignored

            ClearHighlight();
            _ghost.style.display = DisplayStyle.None;
            _panels.FindItem(itemId)?.RemoveFromClassList("inv-item--dragging");
            if (_root.HasPointerCapture(_pointerId))
            {
                _root.ReleasePointer(_pointerId);
            }

            _pointerId = -1;
            if (plan != null && !_model.Execute(itemId, plan) && plan.Message != null)
            {
                Message?.Invoke(plan.Message);
            }
        }
    }
}
