using RaidZero.Items;
using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// The box that appears when the pointer rests on an item: its name, category and the facts ItemTooltip finds
    /// (condition, stack, what's loaded, how full it is), plus the item asset's description. It waits a moment
    /// before showing, follows the pointer, flips to stay on screen, and keeps out of the way while dragging.
    /// </summary>
    internal sealed class InventoryTooltip
    {
        /// <summary>How long the pointer rests on an item before its tooltip shows, in seconds.</summary>
        private const float Delay = 0.4f;

        // From the pointer to the box's nearest corner, in panel units.
        private const float Offset = 18f;

        private readonly VisualElement _root;
        private readonly InventoryDrag _drag;
        private readonly VisualElement _box;
        private readonly Label _title;
        private readonly Label _category;
        private readonly VisualElement _lines;
        private readonly Label _description;

        private InventoryModel _model;
        private VisualElement _hovered;
        private float _hoveredSince;
        private Vector2 _pointer;

        public InventoryTooltip(VisualElement root, InventoryPanels panels, InventoryDrag drag)
        {
            _root = root;
            _drag = drag;

            _box = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip");
            _box.style.position = Position.Absolute;
            _box.style.display = DisplayStyle.None;
            _title = new Label { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__title");
            _category = new Label { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__category");
            _lines = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__lines");
            _description = new Label { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__description");
            _box.Add(_title);
            _box.Add(_category);
            _box.Add(_lines);
            _box.Add(_description);
            _root.Add(_box);

            panels.ItemHovered += OnHovered;
            panels.ItemUnhovered += OnUnhovered;
            panels.ItemPressed += (evt, element) => Hide(forget: true);
            _root.RegisterCallback<PointerMoveEvent>(evt => _pointer = evt.position);

            // A light poll rather than a timer per hover: it also notices the drag starting, the item being
            // redrawn, and the item changing (a magazine loading) while its tooltip is up.
            _root.schedule.Execute(Tick).Every(50);
        }

        public void SetModel(InventoryModel model)
        {
            _model = model;
            Hide(forget: true);
        }

        /// <summary>Takes the tooltip down, as when the screen closes.</summary>
        public void Hide() => Hide(forget: true);

        private void OnHovered(VisualElement element)
        {
            _hovered = element;
            _hoveredSince = Time.unscaledTime;
            Hide(forget: false);
        }

        private void OnUnhovered(VisualElement element)
        {
            if (_hovered == element)
            {
                Hide(forget: true);
            }
        }

        private void Tick()
        {
            // Redrawn panels replace the elements, so the one we were over may have left the screen.
            if (_hovered == null || _hovered.panel == null || _model == null || _drag.IsDragging || !(_hovered.userData is ItemRef item))
            {
                Hide(forget: _hovered == null || _hovered.panel == null);
                return;
            }

            if (Time.unscaledTime - _hoveredSince < Delay)
            {
                return;
            }

            if (!_model.Tree.TryGet(item.ItemId, out var node))
            {
                Hide(forget: true);
                return;
            }

            var description = ItemLibrary.Instance != null ? ItemLibrary.Instance.Find(node.DefId)?.Description : null;
            Fill(ItemTooltip.Build(_model.Catalog, node, description));
            _box.style.display = DisplayStyle.Flex;
            _box.BringToFront();
            Place();
        }

        private void Fill(ItemTooltipContent content)
        {
            _title.text = content.Title;
            _category.text = content.Category;
            _lines.Clear();
            foreach (var line in content.Lines)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__line");
                row.Add(new Label(line.Label) { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__label"));
                row.Add(new Label(line.Value) { pickingMode = PickingMode.Ignore }.WithClass("inv-tooltip__value"));
                _lines.Add(row);
            }

            _lines.style.display = content.Lines.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _description.text = content.Description ?? "";
            _description.style.display = content.Description != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Below and to the right of the pointer, flipped left or up where it would leave the screen. Its size is
        // last frame's layout, which is right after the first frame it shows.
        private void Place()
        {
            var local = _root.WorldToLocal(_pointer);
            var area = _root.layout;
            var size = _box.layout;
            var width = float.IsNaN(size.width) || size.width <= 0 ? 240f : size.width;
            var height = float.IsNaN(size.height) || size.height <= 0 ? 120f : size.height;

            var x = local.x + Offset;
            if (x + width > area.width)
            {
                x = local.x - Offset - width;
            }

            var y = local.y + Offset;
            if (y + height > area.height)
            {
                y = local.y - Offset - height;
            }

            _box.style.left = Mathf.Max(0f, x);
            _box.style.top = Mathf.Max(0f, y);
        }

        private void Hide(bool forget)
        {
            if (forget)
            {
                _hovered = null;
            }

            _box.style.display = DisplayStyle.None;
        }
    }
}
