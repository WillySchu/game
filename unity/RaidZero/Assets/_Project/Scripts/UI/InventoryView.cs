using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// The parts of an inventory screen that don't care where the items come from: a UIDocument of its own, the
    /// window with a gear column and a second column (a crate in a raid, the stash in the Hideout), the status
    /// line, and dragging, splitting and tooltips. InventoryScreen (raids) and HideoutScreen each make one and
    /// give it their InventoryModel; they add their own parts around it (Header, Footer).
    /// </summary>
    internal sealed class InventoryView
    {
        public const string PanelSettingsResource = "InventoryPanelSettings";
        public const string StyleSheetResource = "InventoryScreen";
        private const float MessageSeconds = 4f;

        private readonly InventoryDrag _drag;
        private readonly InventorySplitDialog _split;
        private readonly InventoryTooltip _tooltip;
        private readonly Label _status;
        private InventoryModel _model;
        private float _statusUntil;

        private InventoryView(VisualElement root, string help)
        {
            Root = root;
            Window = new VisualElement().WithClass("inv-window");
            Header = new VisualElement().WithClass("inv-header");
            var columns = new VisualElement().WithClass("inv-columns");
            var gear = new ScrollView(ScrollViewMode.Vertical).WithClass("inv-column").WithClass("inv-column--gear");
            var loot = new ScrollView(ScrollViewMode.Vertical).WithClass("inv-column").WithClass("inv-column--loot");
            gear.style.width = (InventoryPanels.GearColumnCells + 1) * InventoryPanels.Cell;
            loot.style.width = (InventoryPanels.LootColumnCells + 1) * InventoryPanels.Cell;
            columns.Add(gear);
            columns.Add(loot);
            Window.Add(Header);
            Window.Add(columns);

            _status = new Label().WithClass("inv-status");
            Window.Add(_status);
            Footer = new VisualElement().WithClass("inv-footer");
            Window.Add(Footer);
            Window.Add(new Label(help).WithClass("inv-help"));
            Root.Add(Window);

            Panels = new InventoryPanels(gear, loot);
            _drag = new InventoryDrag(Root, Window, Panels);
            _drag.Message += ShowMessage;
            _split = new InventorySplitDialog(Root);
            _split.Message += ShowMessage;
            _drag.SplitRequested += (itemId, rotated, target) => _split.Show(_model, itemId, rotated, target);
            _tooltip = new InventoryTooltip(Root, Panels, _drag);
        }

        /// <summary>Fills the screen; the dimmed backdrop around the window is what "outside" means for drops.</summary>
        public VisualElement Root { get; }

        public VisualElement Window { get; }

        /// <summary>Above the columns, empty unless the owner adds something (the Hideout's player bar).</summary>
        public VisualElement Header { get; }

        /// <summary>Below the status line, empty unless the owner adds something (the Hideout's Deploy).</summary>
        public VisualElement Footer { get; }

        public InventoryPanels Panels { get; }

        public InventoryModel Model => _model;

        public bool IsDragging => _drag.IsDragging;

        public bool IsSplitOpen => _split.IsOpen;

        /// <summary>
        /// Makes the view on a hidden child of <paramref name="owner"/> with its own UIDocument, styled from
        /// Resources. Null, with an error logged, if the panel settings are missing.
        /// </summary>
        public static InventoryView Create(Transform owner, string objectName, string help)
        {
            var settings = Resources.Load<PanelSettings>(PanelSettingsResource);
            if (settings == null)
            {
                Debug.LogError("[inventory] Resources/" + PanelSettingsResource + " is missing, so " + objectName + " is off.");
                return null;
            }

            // The document goes on its own inactive child so its panel settings are set before it first enables.
            var holder = new GameObject(objectName);
            holder.SetActive(false);
            holder.transform.SetParent(owner, false);
            var document = holder.AddComponent<UIDocument>();
            document.panelSettings = settings;
            holder.SetActive(true);

            var root = new VisualElement { name = objectName }.WithClass("inv-root");
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.top = 0;
            root.style.right = 0;
            root.style.bottom = 0;
            var sheet = Resources.Load<StyleSheet>(StyleSheetResource);
            if (sheet != null)
            {
                root.styleSheets.Add(sheet);
            }
            else
            {
                Debug.LogWarning("[inventory] Resources/" + StyleSheetResource + ".uss is missing; " + objectName + " will be unstyled.");
            }

            var view = new InventoryView(root, help);
            root.style.display = DisplayStyle.None;
            document.rootVisualElement.Add(root);
            return view;
        }

        public void SetModel(InventoryModel model)
        {
            _model = model;
            _drag.SetModel(model);
            _tooltip.SetModel(model);
        }

        public void Show() => Root.style.display = DisplayStyle.Flex;

        /// <summary>Hides it, dropping any drag, split or tooltip in progress.</summary>
        public void Hide()
        {
            _drag.Cancel();
            _split.Hide();
            _tooltip.Hide();
            Root.style.display = DisplayStyle.None;
        }

        /// <summary>Draws the model again, or the message where the gear would be when there's nothing to show.</summary>
        public void Rebuild(string emptyMessage)
        {
            Panels.Build(_model, emptyMessage);
            _drag.Refresh();
        }

        public void ShowMessage(string sentence)
        {
            if (string.IsNullOrEmpty(sentence))
            {
                return;
            }

            _status.text = sentence;
            _statusUntil = Time.unscaledTime + MessageSeconds;
        }

        /// <summary>
        /// The keys every inventory screen shares, while it's showing: Enter and Escape for the split dialog, R to
        /// turn what's being dragged, Escape to cancel a drag. True if it used the key, so the owner shouldn't.
        /// </summary>
        public bool HandleKeys(Keyboard keyboard)
        {
            if (keyboard == null)
            {
                return false;
            }

            if (_split.IsOpen)
            {
                if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                {
                    _split.Confirm();
                    return true;
                }

                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    _split.Hide();
                    return true;
                }

                // Typing a number mustn't rotate or open anything.
                return !keyboard.tabKey.wasPressedThisFrame;
            }

            if (keyboard.escapeKey.wasPressedThisFrame && _drag.IsDragging)
            {
                _drag.Cancel();
                return true;
            }

            if (keyboard.rKey.wasPressedThisFrame && _drag.IsDragging)
            {
                _drag.Rotate();
                return true;
            }

            return false;
        }

        /// <summary>Clears the status line once its message has been up long enough. Call every frame.</summary>
        public void Tick()
        {
            if (_statusUntil > 0 && Time.unscaledTime > _statusUntil)
            {
                _statusUntil = 0;
                _status.text = "";
            }
        }
    }
}
