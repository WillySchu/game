using System.Collections.Generic;
using RaidZero.Net;
using RaidZero.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor; // UI Toolkit has a Cursor type too

namespace RaidZero.UI
{
    /// <summary>
    /// Escape is first come, first served: whatever uses it (the split dialog, a drag, the raid inventory
    /// closing, a Hideout container window) says so, and the escape menu only opens if nothing did.
    /// </summary>
    public static class EscapeKey
    {
        private static int _usedFrame = -1;

        /// <summary>Escape was pressed this frame, and nothing has used it yet.</summary>
        public static bool Pressed =>
            Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && _usedFrame != Time.frameCount;

        /// <summary>Call when something used this frame's Escape, so nothing after it reacts too.</summary>
        public static void Use() => _usedFrame = Time.frameCount;
    }

    /// <summary>
    /// The escape menu: Escape, when there's nothing else for it to close, opens it; Escape or Resume closes it.
    /// Settings changes the player's settings (GameSettings): mouse sensitivity and invert look, applied as you
    /// change them and saved when you leave the page. Quit leaves the game (in the editor, stops play mode); in a
    /// raid it asks first, since your gear stays in the raid. It doesn't pause anything (the raid goes on), but
    /// while it's open the mouse is free and the player's input is off, as with the inventory. Escape on a page
    /// goes back to the menu.
    ///
    /// On the client's persistent object in core.client, next to the inventory and Hideout screens. It runs after
    /// them each frame, so they get Escape first. It draws above them and the HUD (its own UIDocument, sorted
    /// higher), with the inventory's panel settings and stylesheet.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class EscapeMenu : MonoBehaviour
    {
        private readonly List<PlayerInput> _pausedInputs = new List<PlayerInput>();

        private VisualElement _root;
        private Label _title;
        private VisualElement _buttons;
        private VisualElement _confirm;
        private VisualElement _settings;
        private Slider _sensitivity;
        private Toggle _invertLook;
        private bool _open;
        private Page _page;
        private CursorLockMode _cursorBefore;
        private bool _cursorVisibleBefore;

        public static EscapeMenu Instance { get; private set; }

        /// <summary>Whether the menu is up, for screens and keys that should wait while it is.</summary>
        public static bool IsShowing => Instance != null && Instance._open;

        public void Open()
        {
            if (_open || _root == null)
            {
                return;
            }

            _open = true;
            ShowPage(Page.Main);
            _root.style.display = DisplayStyle.Flex;
            _cursorBefore = Cursor.lockState;
            _cursorVisibleBefore = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            InventoryView.EnsureUiInput();
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

        public void Close()
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            ShowPage(Page.Main); // saves the settings if they were up
            _root.style.display = DisplayStyle.None;

            // Back to how it was in a raid; anywhere else (the Hideout) the mouse stays free.
            bool inRaid = InRaid();
            Cursor.lockState = inRaid ? _cursorBefore : CursorLockMode.None;
            Cursor.visible = inRaid ? _cursorVisibleBefore : true;
            foreach (var input in _pausedInputs)
            {
                if (input != null && input.isActiveAndEnabled)
                {
                    input.ActivateInput();
                }
            }

            _pausedInputs.Clear();
        }

        private void Awake()
        {
            Instance = this;
            var settings = Resources.Load<PanelSettings>(InventoryView.PanelSettingsResource);
            if (settings == null)
            {
                Debug.LogError("[menu] Resources/" + InventoryView.PanelSettingsResource + " is missing, so the escape menu is off.");
                enabled = false;
                return;
            }

            var holder = new GameObject("Escape Menu");
            holder.SetActive(false);
            holder.transform.SetParent(transform, false);
            var document = holder.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 20; // above the HUD (10) and the screens (0)
            holder.SetActive(true);

            // A backdrop over everything, so nothing underneath takes clicks, and the menu in the middle.
            _root = new VisualElement { name = "escape-menu" }.WithClass("esc-root");
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.top = 0;
            _root.style.right = 0;
            _root.style.bottom = 0;
            var sheet = Resources.Load<StyleSheet>(InventoryView.StyleSheetResource);
            if (sheet != null)
            {
                _root.styleSheets.Add(sheet);
            }

            var panel = new VisualElement().WithClass("esc-panel");
            _title = new Label().WithClass("esc-title");
            panel.Add(_title);

            _buttons = new VisualElement().WithClass("esc-buttons");
            _buttons.Add(new Button(Close) { text = "Resume" }.WithClass("esc-button"));
            _buttons.Add(new Button(() => ShowPage(Page.Settings)) { text = "Settings" }.WithClass("esc-button"));
            _buttons.Add(new Button(OnQuitClicked) { text = "Quit" }.WithClass("esc-button"));
            panel.Add(_buttons);

            _confirm = new VisualElement().WithClass("esc-buttons");
            _confirm.Add(new Label("Quit in the middle of a raid? Your gear stays in it: you can rejoin while it lasts, or it's lost when it ends.").WithClass("esc-text"));
            _confirm.Add(new Button(Quit) { text = "Quit" }.WithClass("esc-button").WithClass("esc-button--danger"));
            _confirm.Add(new Button(() => ShowPage(Page.Main)) { text = "Back" }.WithClass("esc-button"));
            panel.Add(_confirm);

            _settings = BuildSettings();
            panel.Add(_settings);

            _root.Add(panel);
            _root.style.display = DisplayStyle.None;
            document.rootVisualElement.Add(_root);
        }

        private void OnDestroy()
        {
            if (_open)
            {
                Close();
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (!EscapeKey.Pressed)
            {
                return;
            }

            EscapeKey.Use();
            if (!_open)
            {
                Open();
            }
            else if (_page != Page.Main)
            {
                ShowPage(Page.Main);
            }
            else
            {
                Close();
            }
        }

        // Keeps the mouse free while it's open, whatever else asks for it (the player locks it when spawning).
        private void LateUpdate()
        {
            if (_open && (Cursor.lockState != CursorLockMode.None || !Cursor.visible))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private enum Page
        {
            Main,
            Confirm,
            Settings,
        }

        private void ShowPage(Page page)
        {
            if (_page == Page.Settings && page != Page.Settings)
            {
                GameSettings.Save();
            }

            _page = page;
            _title.text = page == Page.Settings ? "Settings" : "Raid Zero";
            _buttons.style.display = page == Page.Main ? DisplayStyle.Flex : DisplayStyle.None;
            _confirm.style.display = page == Page.Confirm ? DisplayStyle.Flex : DisplayStyle.None;
            _settings.style.display = page == Page.Settings ? DisplayStyle.Flex : DisplayStyle.None;
            if (page == Page.Settings)
            {
                ShowSettings();
            }
        }

        // Each control changes its setting as it moves, so the next look around feels the change.
        private VisualElement BuildSettings()
        {
            var page = new VisualElement().WithClass("esc-settings");

            _sensitivity = new Slider("Mouse sensitivity", GameSettings.MinMouseSensitivity, GameSettings.MaxMouseSensitivity) { showInputField = true }
                .WithClass("esc-setting");
            _sensitivity.RegisterValueChangedCallback(evt =>
            {
                // Two decimals is fine enough, and keeps the box readable.
                GameSettings.MouseSensitivity = Mathf.Round(evt.newValue * 100f) / 100f;
                _sensitivity.SetValueWithoutNotify(GameSettings.MouseSensitivity);
            });
            page.Add(_sensitivity);

            _invertLook = new Toggle("Invert look").WithClass("esc-setting");
            _invertLook.RegisterValueChangedCallback(evt => GameSettings.InvertLook = evt.newValue);
            page.Add(_invertLook);

            var buttons = new VisualElement().WithClass("esc-buttons").WithClass("esc-settings__buttons");
            buttons.Add(new Button(() =>
            {
                GameSettings.ResetToDefaults();
                ShowSettings();
            }) { text = "Reset to defaults" }.WithClass("esc-button"));
            buttons.Add(new Button(() => ShowPage(Page.Main)) { text = "Back" }.WithClass("esc-button"));
            page.Add(buttons);
            return page;
        }

        // The controls, from the settings as they are now.
        private void ShowSettings()
        {
            _sensitivity.SetValueWithoutNotify(GameSettings.MouseSensitivity);
            _invertLook.SetValueWithoutNotify(GameSettings.InvertLook);
        }

        private void OnQuitClicked()
        {
            if (InRaid())
            {
                ShowPage(Page.Confirm);
            }
            else
            {
                Quit();
            }
        }

        private static void Quit()
        {
            Debug.Log("[menu] Quitting.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private static bool InRaid() => RaidInventoryClient.Instance != null && RaidInventoryClient.Instance.View.Character != null;
    }
}
