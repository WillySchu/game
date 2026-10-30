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
    /// (Cmd+click on a Mac) quick-moves an item; Shift+drag splits a stack, asking how many; double-click opens a
    /// container that's inside another. Resting the pointer on an item shows its tooltip. F opens the nearest
    /// crate (a stand-in until interaction exists).
    ///
    /// The rules all live in shared code (InventoryModel over the raid's RaidInventorySource), and the window,
    /// dragging and tooltips in InventoryView, which the Hideout screen shares; this adds the raid's parts: Tab,
    /// the player's input, crates. Put it on any GameObject in the raid scene. It makes its own UIDocument from
    /// Resources/InventoryPanelSettings and styles itself with Resources/InventoryScreen.uss.
    /// </summary>
    public sealed class InventoryScreen : MonoBehaviour
    {
        private const string UiActionMap = "UI";
        private const string Help = "Tab close   ·   drag to move   ·   R rotate while dragging   ·   Shift+drag split   ·   Ctrl+click quick move   ·   double-click open   ·   drag out of the window to drop   ·   F open nearest crate";

        [SerializeField]
        [Tooltip("How close the nearest crate must be for F to open it, in metres.")]
        private float _openRange = 2.5f;

        private readonly List<KeyValuePair<PlayerInput, string>> _switchedInputs = new List<KeyValuePair<PlayerInput, string>>();

        private InventoryView _view;
        private ItemCatalog _catalog;
        private RaidInventorySource _source;
        private InventoryModel _model;

        private bool _open;
        private bool _dirty;
        private CursorLockMode _cursorBefore;
        private bool _cursorVisibleBefore;

        public bool IsOpen => _open;

        public void Open()
        {
            if (_open || _view == null)
            {
                return;
            }

            EnsureModel();
            _open = true;
            _view.Show();
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

            _view.Hide();
            _open = false;
            Cursor.lockState = _cursorBefore;
            Cursor.visible = _cursorVisibleBefore;
            RestoreInputs();

            // Looting ends with the screen, as it does in most games: close whatever was open.
            if (_model != null)
            {
                foreach (var itemId in new List<string>(_model.OpenedItems))
                {
                    _model.CloseItem(itemId);
                }

                foreach (var other in new List<InventoryRoot>(_model.Others))
                {
                    _model.Close(other.Root.Id);
                }
            }
        }

        private void Awake()
        {
            _view = InventoryView.Create(transform, "Inventory UI", Help);
            if (_view == null)
            {
                enabled = false;
                return;
            }

            _view.Panels.CloseClicked += OnCloseClicked;
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
            if (_view == null)
            {
                return;
            }

            // The raid client can appear after this does (scene load order); pick it up when it's there.
            if (_model == null && RaidInventoryClient.Instance != null)
            {
                EnsureModel();
            }

            var keyboard = Keyboard.current;
            if (_open && _view.HandleKeys(keyboard))
            {
                // The view used the key (the split dialog, or turning or cancelling a drag).
            }
            else if (keyboard != null)
            {
                if (keyboard.tabKey.wasPressedThisFrame)
                {
                    if (_open)
                    {
                        Close();
                    }
                    else if (!HideoutShowing())
                    {
                        Open();
                    }
                }
                else if (keyboard.escapeKey.wasPressedThisFrame && _open)
                {
                    Close();
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

            _view.Tick();
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
            _model.Refused += _view.ShowMessage;
            _view.SetModel(_model);
            _dirty = true;
        }

        private void DisposeModel()
        {
            if (_model != null)
            {
                _model.Changed -= OnModelChanged;
                _model.Refused -= _view.ShowMessage;
                _model.Dispose();
                _model = null;
            }

            _source?.Dispose();
            _source = null;
        }

        private void OnCloseClicked(string id)
        {
            if (_model == null)
            {
                return;
            }

            if (_model.IsOpenedItem(id))
            {
                _model.CloseItem(id);
            }
            else
            {
                _model.Close(id);
            }
        }

        // In the Hideout, Tab (typing in the name box, say) mustn't open the raid's screen over it.
        private static bool HideoutShowing()
        {
            var hideout = FindAnyObjectByType<HideoutScreen>();
            return hideout != null && hideout.IsShown;
        }

        // Rebuilt on the next frame rather than inside the event, so a burst of updates is drawn once.
        private void OnModelChanged() => _dirty = true;

        private void Rebuild()
        {
            _dirty = false;
            _view.Rebuild(RaidInventoryClient.Instance == null ? "Not connected to a raid." : "Waiting for your gear: join a raid first.");
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
                _view.ShowMessage("There's no crate within " + _openRange + " m.");
                return;
            }

            client.Open(nearest.ContainerId);
            Open();
        }
    }
}
