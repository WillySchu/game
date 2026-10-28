using System.Collections.Generic;
using RaidZero.Net;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor; // UI Toolkit has a Cursor type too

namespace RaidZero.UI
{
    /// <summary>
    /// The raid inventory screen. Tab opens and closes it; while it's open the mouse is free and the player's
    /// input switches to the "UI" action map, so moving the mouse doesn't turn the camera.
    ///
    /// Drag items between grids, slots and containers; R turns the item you're dragging; let go over another
    /// item to stack, load or put it inside; let go outside the window to drop it on the ground. Ctrl+click
    /// (Cmd+click on a Mac) quick-moves an item. F opens the nearest crate (a stand-in until interaction exists).
    ///
    /// The rules all live in shared code (InventoryModel over the raid's RaidInventorySource), so this only
    /// builds elements and forwards input. Put it on any GameObject in the raid scene. It makes its own
    /// UIDocument from Resources/InventoryPanelSettings and styles itself with Resources/InventoryScreen.uss.
    /// </summary>
    public sealed class InventoryScreen : MonoBehaviour
    {
        private const string PanelSettingsResource = "InventoryPanelSettings";
        private const string StyleSheetResource = "InventoryScreen";
        private const string UiActionMap = "UI";
        private const float MessageSeconds = 4f;

        [SerializeField]
        [Tooltip("How close the nearest crate must be for F to open it, in metres.")]
        private float _openRange = 2.5f;

        private readonly List<KeyValuePair<PlayerInput, string>> _switchedInputs = new List<KeyValuePair<PlayerInput, string>>();

        private UIDocument _document;
        private VisualElement _root;
        private Label _status;
        private InventoryPanels _panels;
        private InventoryDrag _drag;

        private ItemCatalog _catalog;
        private RaidInventorySource _source;
        private InventoryModel _model;

        private bool _open;
        private bool _dirty;
        private float _statusUntil;
        private CursorLockMode _cursorBefore;
        private bool _cursorVisibleBefore;

        public bool IsOpen => _open;

        public void Open()
        {
            if (_open || _root == null)
            {
                return;
            }

            EnsureModel();
            _open = true;
            _root.style.display = DisplayStyle.Flex;
            _cursorBefore = Cursor.lockState;
            _cursorVisibleBefore = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SwitchInputs();
            Rebuild();
        }

        public void Close()
        {
            if (!_open)
            {
                return;
            }

            _drag.Cancel();
            _open = false;
            _root.style.display = DisplayStyle.None;
            Cursor.lockState = _cursorBefore;
            Cursor.visible = _cursorVisibleBefore;
            RestoreInputs();

            // Looting ends with the screen, as it does in most games: close whatever was open.
            if (_model != null)
            {
                foreach (var other in new List<InventoryRoot>(_model.Others))
                {
                    _model.Close(other.Root.Id);
                }
            }
        }

        private void Awake()
        {
            var settings = Resources.Load<PanelSettings>(PanelSettingsResource);
            if (settings == null)
            {
                Debug.LogError("[inventory] Resources/" + PanelSettingsResource + " is missing, so the inventory screen is off.");
                enabled = false;
                return;
            }

            // The document goes on its own inactive child so its panel settings are set before it first enables.
            var holder = new GameObject("Inventory UI");
            holder.SetActive(false);
            holder.transform.SetParent(transform, false);
            _document = holder.AddComponent<UIDocument>();
            _document.panelSettings = settings;
            holder.SetActive(true);

            // Fills the screen even without the stylesheet: the backdrop is what "outside the window" means.
            _root = new VisualElement { name = "inventory-root" }.WithClass("inv-root");
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.top = 0;
            _root.style.right = 0;
            _root.style.bottom = 0;
            var sheet = Resources.Load<StyleSheet>(StyleSheetResource);
            if (sheet != null)
            {
                _root.styleSheets.Add(sheet);
            }
            else
            {
                Debug.LogWarning("[inventory] Resources/" + StyleSheetResource + ".uss is missing; the screen will be unstyled.");
            }

            var window = new VisualElement().WithClass("inv-window");
            var columns = new VisualElement().WithClass("inv-columns");
            var gear = new ScrollView(ScrollViewMode.Vertical).WithClass("inv-column").WithClass("inv-column--gear");
            var loot = new ScrollView(ScrollViewMode.Vertical).WithClass("inv-column").WithClass("inv-column--loot");
            gear.style.width = (InventoryPanels.GearColumnCells + 1) * InventoryPanels.Cell;
            loot.style.width = (InventoryPanels.LootColumnCells + 1) * InventoryPanels.Cell;
            columns.Add(gear);
            columns.Add(loot);
            window.Add(columns);

            _status = new Label().WithClass("inv-status");
            window.Add(_status);
            window.Add(new Label("Tab close   ·   drag to move   ·   R rotate while dragging   ·   Ctrl+click quick move   ·   drag out of the window to drop   ·   F open nearest crate")
                .WithClass("inv-help"));
            _root.Add(window);

            _panels = new InventoryPanels(gear, loot);
            _panels.CloseClicked += id => _model?.Close(id);
            _drag = new InventoryDrag(_root, window, _panels);
            _drag.Message += ShowMessage;

            _root.style.display = DisplayStyle.None;
            _document.rootVisualElement.Add(_root);
        }

        private void OnDestroy()
        {
            if (_open)
            {
                Close();
            }

            DisposeModel();
        }

        private void Update()
        {
            if (_root == null)
            {
                return;
            }

            // The raid client can appear after this does (scene load order); pick it up when it's there.
            if (_model == null && RaidInventoryClient.Instance != null)
            {
                EnsureModel();
            }

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.tabKey.wasPressedThisFrame)
                {
                    if (_open)
                    {
                        Close();
                    }
                    else
                    {
                        Open();
                    }
                }
                else if (keyboard.escapeKey.wasPressedThisFrame && _open)
                {
                    if (_drag.IsDragging)
                    {
                        _drag.Cancel();
                    }
                    else
                    {
                        Close();
                    }
                }
                else if (keyboard.rKey.wasPressedThisFrame && _open)
                {
                    _drag.Rotate();
                }
                else if (keyboard.fKey.wasPressedThisFrame)
                {
                    OpenNearestCrate();
                }
            }

            if (_open && _dirty)
            {
                Rebuild();
            }

            if (_statusUntil > 0 && Time.unscaledTime > _statusUntil)
            {
                _statusUntil = 0;
                _status.text = "";
            }
        }

        private void EnsureModel()
        {
            var client = RaidInventoryClient.Instance;
            if (_model != null || client == null)
            {
                return;
            }

            if (_catalog == null)
            {
                try
                {
                    _catalog = ServerConfig.LoadCatalog();
                }
                catch (System.Exception exception)
                {
                    Debug.LogError("[inventory] Couldn't load the item catalog: " + exception.Message);
                    enabled = false;
                    return;
                }
            }

            _source = client.CreateSource();
            _model = new InventoryModel(_catalog, _source);
            _model.Changed += OnModelChanged;
            _model.Refused += ShowMessage;
            _drag.SetModel(_model);
            _dirty = true;
        }

        private void DisposeModel()
        {
            if (_model != null)
            {
                _model.Changed -= OnModelChanged;
                _model.Refused -= ShowMessage;
                _model.Dispose();
                _model = null;
            }

            _source?.Dispose();
            _source = null;
        }

        // Rebuilt on the next frame rather than inside the event, so a burst of updates is drawn once.
        private void OnModelChanged() => _dirty = true;

        private void Rebuild()
        {
            _dirty = false;
            _panels.Build(_model, RaidInventoryClient.Instance == null ? "Not connected to a raid." : "Waiting for your gear: join a raid first.");
            _drag.Refresh();
        }

        private void ShowMessage(string sentence)
        {
            if (string.IsNullOrEmpty(sentence) || _status == null)
            {
                return;
            }

            _status.text = sentence;
            _statusUntil = Time.unscaledTime + MessageSeconds;
        }

        // Stop the character walking and looking around while the screen is up, and put it back after.
        // PlayerInput.all holds only enabled PlayerInputs, which in a raid is the local player's.
        private void SwitchInputs()
        {
            _switchedInputs.Clear();
            foreach (var input in PlayerInput.all)
            {
                var map = input.currentActionMap;
                if (map != null && map.name != UiActionMap && input.actions != null && input.actions.FindActionMap(UiActionMap) != null)
                {
                    _switchedInputs.Add(new KeyValuePair<PlayerInput, string>(input, map.name));
                    input.SwitchCurrentActionMap(UiActionMap);
                }
            }
        }

        private void RestoreInputs()
        {
            foreach (var pair in _switchedInputs)
            {
                if (pair.Key != null)
                {
                    pair.Key.SwitchCurrentActionMap(pair.Value);
                }
            }

            _switchedInputs.Clear();
        }

        // Temporary: interaction (look at a crate, press E) belongs to gameplay. Until then, F opens whatever
        // crate is nearest the camera, and the screen with it.
        private void OpenNearestCrate()
        {
            var client = RaidInventoryClient.Instance;
            if (client == null || client.View.Character == null)
            {
                return;
            }

            var from = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            LootContainer nearest = null;
            float best = _openRange;
            foreach (var crate in FindObjectsByType<LootContainer>(FindObjectsSortMode.None))
            {
                var distance = Vector3.Distance(from, crate.transform.position);
                if (distance <= best)
                {
                    best = distance;
                    nearest = crate;
                }
            }

            if (nearest == null)
            {
                ShowMessage("There's no crate within " + _openRange + " m.");
                return;
            }

            client.Open(nearest.ContainerId);
            Open();
        }
    }
}
