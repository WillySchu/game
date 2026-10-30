using System;
using RaidZero.Shared.InventoryUi;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// "How many?" after a shift-drag: a slider with a number box, Split and Cancel. Enter splits and Escape
    /// cancels (InventoryScreen forwards the keys), as does clicking outside the box. The drop is planned
    /// again with the chosen number when it's confirmed, so anything the server changed meanwhile is taken
    /// into account; all of the stack is an ordinary move.
    /// </summary>
    internal sealed class InventorySplitDialog
    {
        private readonly VisualElement _overlay;
        private readonly Label _title;
        private readonly SliderInt _amount;

        private InventoryModel _model;
        private string _itemId;
        private bool _rotated;
        private DropTarget _target;

        public InventorySplitDialog(VisualElement root)
        {
            _overlay = new VisualElement().WithClass("inv-split");
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0;
            _overlay.style.top = 0;
            _overlay.style.right = 0;
            _overlay.style.bottom = 0;
            _overlay.style.display = DisplayStyle.None;
            _overlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _overlay)
                {
                    Hide();
                }
            });

            var box = new VisualElement().WithClass("inv-split__box");
            _title = new Label().WithClass("inv-split__title");
            _amount = new SliderInt(1, 2) { showInputField = true }.WithClass("inv-split__amount");
            var buttons = new VisualElement().WithClass("inv-split__buttons");
            buttons.Add(new Button(Hide) { text = "Cancel" }.WithClass("inv-split__cancel"));
            buttons.Add(new Button(Confirm) { text = "Split" }.WithClass("inv-split__ok"));
            box.Add(_title);
            box.Add(_amount);
            box.Add(buttons);
            _overlay.Add(box);
            root.Add(_overlay);
        }

        /// <summary>The chosen split was refused: a sentence for the status line.</summary>
        public event Action<string> Message;

        public bool IsOpen => _itemId != null;

        public void Show(InventoryModel model, string itemId, bool rotated, DropTarget target)
        {
            if (model == null || !model.Tree.TryGet(itemId, out var item) || !model.Catalog.TryGet(item.DefId, out var def))
            {
                return;
            }

            _model = model;
            _itemId = itemId;
            _rotated = rotated;
            _target = target;

            _title.text = "Split " + (string.IsNullOrEmpty(def.DisplayName) ? def.DefId : def.DisplayName) + " (" + item.Quantity + "): how many?";
            _amount.lowValue = 1;
            _amount.highValue = item.Quantity;
            _amount.value = Math.Max(1, item.Quantity / 2);
            _overlay.style.display = DisplayStyle.Flex;
            _overlay.BringToFront();
        }

        public void Confirm()
        {
            if (!IsOpen)
            {
                return;
            }

            var itemId = _itemId;
            var plan = _model.Plan(itemId, _rotated, _target, _amount.value);
            Hide();
            if (!_model.Execute(itemId, plan) && plan.Message != null)
            {
                Message?.Invoke(plan.Message);
            }
        }

        public void Hide()
        {
            _itemId = null;
            _target = null;
            _overlay.style.display = DisplayStyle.None;
        }
    }
}
