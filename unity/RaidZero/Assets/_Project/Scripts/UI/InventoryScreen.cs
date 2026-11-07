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
    /// input is off, so moving the mouse doesn't turn the camera.
    ///
    /// Drag items between grids, slots and containers; R turns the item you're dragging; let go over another
    /// item to stack, load or put it inside; let go outside the window to drop it on the ground. Ctrl+click
    /// (Cmd+click on a Mac) quick-moves an item; Shift+drag splits a stack, asking how many; double-click opens a
    /// container that's inside another. Resting the pointer on an item shows its tooltip. Crates and items on
    /// the ground are opened and taken by looking at them and pressing F (<see cref="WorldInteraction"/>).
    ///
    /// The rules all live in shared code (InventoryModel over the raid's RaidInventorySource), and the window,
    /// dragging and tooltips in InventoryView, which the Hideout screen shares; this adds the raid's parts: Tab
    /// and the player's input. Put it on any GameObject in the raid scene. It makes its own UIDocument from
    /// Resources/InventoryPanelSettings and styles itself with Resources/InventoryScreen.uss.
    /// </summary>
    public sealed class InventoryScreen : MonoBehaviour
    {
        private const string Help = "Tab close   ·   drag to move   ·   R rotate while dragging   ·   Shift+drag split   ·   Ctrl+click quick move   ·   double-click open   ·   drag out of the window to drop";

        private readonly List<PlayerInput> _pausedInputs = new List<PlayerInput>();

        private InventoryView _view;
        private ItemCatalog _catalog;
        private RaidInventorySource _source;
        private InventoryModel _model;

        private bool _open;
        private bool _dirty;
        private CursorLockMode _cursorBefore;
        private bool _cursorVisibleBefore;

        public bool IsOpen => _open;

        /// <summary>A sentence on the status line for a few seconds, such as why something couldn't be taken.</summary>
        public void ShowMessage(string sentence) => _view?.ShowMessage(sentence);

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
            InventoryView.EnsureUiInput();
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
            // Back to how it was, unless the raid is over (the screen closes itself when the player dies or
            // extracts with it open): then the Hideout needs the mouse.
            var inRaid = RaidInventoryClient.Instance != null && RaidInventoryClient.Instance.View.Character != null;
            Cursor.lockState = inRaid ? _cursorBefore : CursorLockMode.None;
            Cursor.visible = inRaid ? _cursorVisibleBefore : true;
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
        // The player's input goes quiet while the screen is open: their current action map is turned off,
        // and on again when it closes. Not by switching them to the "UI" map, as this used to: the first player's
        // PlayerInput uses the project-wide actions themselves, and UI Toolkit reads clicks from that asset's UI
        // map, so switching back to "Player" turned UI clicks off everywhere, the Hideout included.
        private void SwitchInputs()
        {
            _pausedInputs.Clear();
            foreach (var input in PlayerInput.all)
            {
                if (input.inputIsActive)
                {
                    _pausedInputs.Add(input);
                    input.DeactivateInput();
                }
            }
        }

        private void RestoreInputs()
        {
            foreach (var input in _pausedInputs)
            {
                if (input != null && input.isActiveAndEnabled)
                {
                    input.ActivateInput();
                }
            }

            _pausedInputs.Clear();
        }
    }
}
