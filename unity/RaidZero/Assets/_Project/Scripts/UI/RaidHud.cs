using FishNet.Managing;
using RaidZero.Net;
using RaidZero.Shared;
using RaidZero.Shared.Raid;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// What's on screen during a raid: the raid clock at the top, the extraction countdown while you stand in an
    /// extraction zone, your health (bottom left), the rounds loaded and fire mode of the gun in hand (bottom right, from the local
    /// PlayerController), and a card when you leave (Extracted, Killed, Missing in action, Raid called off) that
    /// stays up for a few seconds over the Hideout coming back. Everything comes from RaidSessionClient; the
    /// wording is in RaidHudText.
    ///
    /// Put it on any GameObject in the raid scene. It needs a RaidSessionClient on the NetworkManager and adds one
    /// if there isn't one. It draws above the inventory and Hideout screens (its UIDocument sorts higher), so the
    /// clock stays visible while looting.
    /// </summary>
    public sealed class RaidHud : MonoBehaviour
    {
        /// <summary>The last stretch of the clock, in seconds, when it turns to its warning colour.</summary>
        private const float LateSeconds = 60f;
        private const float HurtFlashSeconds = 0.35f;

        [SerializeField]
        [Tooltip("How long the card stays up after you leave a raid, in seconds.")]
        private float _exitCardSeconds = 5f;

        private NetworkManager _networkManager;
        private RaidSessionClient _session;
        private VisualElement _root;
        private Label _clock;
        private VisualElement _extraction;
        private Label _extractionLine;
        private VisualElement _extractionFill;
        private VisualElement _card;
        private Label _cardTitle;
        private Label _cardDetail;
        private VisualElement _prompt;
        private Label _promptKey;
        private Label _promptText;
        private VisualElement _health;
        private VisualElement _healthFill;
        private Label _healthValue;
        private float _hurtUntil;
        private VisualElement _ammo;
        private Label _ammoRounds;
        private Label _ammoMode;

        private string _lastPoint;
        private float _extractionTotal;
        private float _cardUntil;
        private bool _warnedNoSession;

        private void Awake()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            var settings = Resources.Load<PanelSettings>(InventoryView.PanelSettingsResource);
            if (settings == null)
            {
                Debug.LogError("[hud] Resources/" + InventoryView.PanelSettingsResource + " is missing, so the raid HUD is off.");
                enabled = false;
                return;
            }

            var holder = new GameObject("Raid HUD");
            holder.SetActive(false);
            holder.transform.SetParent(transform, false);
            var document = holder.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 10;
            holder.SetActive(true);

            _root = new VisualElement { name = "raid-hud", pickingMode = PickingMode.Ignore }.WithClass("hud");
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

            _clock = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-clock");
            _root.Add(_clock);

            _extraction = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-extraction");
            _extractionLine = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-extraction__line");
            var bar = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-extraction__bar");
            _extractionFill = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-extraction__fill");
            bar.Add(_extractionFill);
            _extraction.Add(_extractionLine);
            _extraction.Add(bar);
            _root.Add(_extraction);

            // What F does, from WorldInteraction, just under the middle of the screen.
            _prompt = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-prompt");
            _promptKey = new Label("F") { pickingMode = PickingMode.Ignore }.WithClass("hud-prompt__key");
            _promptText = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-prompt__text");
            _prompt.Add(_promptKey);
            _prompt.Add(_promptText);
            _root.Add(_prompt);

            // Health, bottom left: a bar and the number.
            _health = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-health");
            var healthBar = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-health__bar");
            _healthFill = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-health__fill");
            healthBar.Add(_healthFill);
            _healthValue = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-health__value");
            _health.Add(healthBar);
            _health.Add(_healthValue);
            _root.Add(_health);

            // The gun in hand, bottom right: the rounds loaded, as the player controller predicts them, and the
            // fire mode.
            _ammo = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-ammo");
            _ammoRounds = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-ammo__rounds");
            _ammoMode = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-ammo__mode");
            _ammo.Add(_ammoRounds);
            _ammo.Add(_ammoMode);
            _root.Add(_ammo);

            _card = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-card");
            _cardTitle = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-card__title");
            _cardDetail = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-card__detail");
            _card.Add(_cardTitle);
            _card.Add(_cardDetail);
            _root.Add(_card);

            _clock.style.display = DisplayStyle.None;
            _extraction.style.display = DisplayStyle.None;
            _prompt.style.display = DisplayStyle.None;
            _health.style.display = DisplayStyle.None;
            _ammo.style.display = DisplayStyle.None;
            _card.style.display = DisplayStyle.None;
            document.rootVisualElement.Add(_root);
        }

        private void OnDestroy() => Unhook();

        private void Update()
        {
            if (_root == null)
            {
                return;
            }

            if (_session == null)
            {
                Hook();
            }

            var inRaid = _session != null && _networkManager != null && _networkManager.IsClientStarted && !_session.Exit.HasValue;
            ShowClock(inRaid);
            ShowExtraction(inRaid);
            ShowPrompt(inRaid);
            ShowHealth(inRaid);
            ShowAmmo(inRaid);

            if (_cardUntil > 0 && Time.unscaledTime > _cardUntil)
            {
                _cardUntil = 0;
                _card.style.display = DisplayStyle.None;
            }
        }

        // The session lives on the NetworkManager. Older scenes don't have one there, so add it rather than ask
        // everyone to edit the prefab.
        private void Hook()
        {
            if (_networkManager == null)
            {
                _networkManager = FindAnyObjectByType<NetworkManager>();
                if (_networkManager == null)
                {
                    return;
                }
            }

            _session = RaidSessionClient.Instance;
            if (_session == null)
            {
                _session = _networkManager.GetComponent<RaidSessionClient>();
            }

            // The client setup (SetupClientComponents) adds it. Without it there's no clock to show, so say so
            // once rather than failing every frame.
            if (_session == null)
            {
                if (!_warnedNoSession)
                {
                    Debug.LogWarning("[hud] No RaidSessionClient on the NetworkManager, so the raid HUD stays hidden. Start from the core scene, or check SetupClientComponents ran.");
                    _warnedNoSession = true;
                }

                return;
            }

            _session.LeftRaid += OnLeftRaid;
            _session.ExtractionChanged += OnExtractionChanged;
            _session.HealthChanged += OnHealthChanged;
        }

        private void Unhook()
        {
            if (_session != null)
            {
                _session.LeftRaid -= OnLeftRaid;
                _session.ExtractionChanged -= OnExtractionChanged;
                _session.HealthChanged -= OnHealthChanged;
                _session = null;
            }
        }

        private void ShowClock(bool inRaid)
        {
            var show = inRaid && _session.Phase == RaidPhase.InProgress;
            _clock.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }

            var left = _session.SecondsRemaining;
            _clock.text = RaidHudText.Clock(left);
            _clock.EnableInClassList("hud-clock--late", left <= LateSeconds);
        }

        // The player's health while they're in the raid, red when low, flashing for a moment when it drops.
        private void ShowHealth(bool inRaid)
        {
            var show = inRaid && _session.Phase != RaidPhase.Closed;
            _health.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }

            var health = _session.Health;
            var max = _session.MaxHealth;
            _healthFill.style.width = Length.Percent(100f * RaidHudText.HealthFill(health, max));
            _healthValue.text = RaidHudText.Health(health);
            _health.EnableInClassList("hud-health--low", RaidHudText.IsLowHealth(health, max));
            _health.EnableInClassList("hud-health--hurt", Time.unscaledTime < _hurtUntil);
        }

        private void OnHealthChanged(float before, float now)
        {
            if (now < before)
            {
                _hurtUntil = Time.unscaledTime + HurtFlashSeconds;
            }
        }

        // The local player's gun, while they have one in hand.
        private void ShowAmmo(bool inRaid)
        {
            var player = inRaid ? PlayerController.Local : null;
            var show = player != null && player.HasGun;
            _ammo.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }

            _ammoRounds.text = RaidHudText.Rounds(player.LoadedRounds);
            _ammoRounds.EnableInClassList("hud-ammo__rounds--empty", player.LoadedRounds <= 0);
            _ammoMode.text = RaidHudText.FireMode(player.FireModeName);
        }

        // The prompt when there's something to use; otherwise, for a moment, what happened when F was pressed.
        private void ShowPrompt(bool inRaid)
        {
            var interaction = WorldInteraction.Instance;
            var prompt = inRaid && interaction != null ? interaction.Prompt : null;
            var message = inRaid && interaction != null && prompt == null ? interaction.Message : null;
            _prompt.style.display = prompt != null || message != null ? DisplayStyle.Flex : DisplayStyle.None;
            _promptKey.style.display = prompt != null ? DisplayStyle.Flex : DisplayStyle.None;
            _promptText.text = prompt ?? message ?? "";
            _prompt.EnableInClassList("hud-prompt--message", prompt == null);
        }

        private void ShowExtraction(bool inRaid)
        {
            var show = inRaid && _session.IsExtracting;
            _extraction.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }

            var left = _session.ExtractionSecondsLeft;
            _extractionLine.text = RaidHudText.ExtractionLine(_session.ExtractionPoint, left);
            _extractionFill.style.width = Length.Percent(100f * RaidHudText.ExtractionProgress(left, _extractionTotal));
        }

        // The server sends the countdown when it starts and when damage starts it over, both times at its full
        // length, so that's the bar's whole width.
        private void OnExtractionChanged()
        {
            if (_session.IsExtracting)
            {
                _lastPoint = _session.ExtractionPoint;
                _extractionTotal = _session.ExtractionSecondsLeft;
            }
        }

        private void OnLeftRaid(RaidExit exit)
        {
            var (title, detail) = RaidHudText.ExitCard(exit, exit == RaidExit.Extracted ? _lastPoint : null);
            _cardTitle.text = title;
            _cardDetail.text = detail;
            _card.ClearClassList();
            _card.AddToClassList("hud-card");
            _card.AddToClassList(exit == RaidExit.Extracted ? "hud-card--extracted" : exit == RaidExit.Aborted ? "hud-card--aborted" : "hud-card--lost");
            _card.style.display = DisplayStyle.Flex;
            _cardUntil = Time.unscaledTime + _exitCardSeconds;
            _lastPoint = null;
        }
    }
}
