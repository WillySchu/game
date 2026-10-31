using FishNet.Managing;
using RaidZero.Net;
using RaidZero.Shared.Raid;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// What's on screen during a raid: the raid clock at the top, the extraction countdown while you stand in an
    /// extraction zone, and a card when you leave (Extracted, Killed, Missing in action, Raid called off) that
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

        private string _lastPoint;
        private float _extractionTotal;
        private float _cardUntil;

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

            _card = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("hud-card");
            _cardTitle = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-card__title");
            _cardDetail = new Label { pickingMode = PickingMode.Ignore }.WithClass("hud-card__detail");
            _card.Add(_cardTitle);
            _card.Add(_cardDetail);
            _root.Add(_card);

            _clock.style.display = DisplayStyle.None;
            _extraction.style.display = DisplayStyle.None;
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

            if (_session == null)
            {
                Debug.Log("[hud] No RaidSessionClient on the NetworkManager; adding one so the HUD has a clock to show.");
                _session = _networkManager.gameObject.AddComponent<RaidSessionClient>();
            }

            _session.LeftRaid += OnLeftRaid;
            _session.ExtractionChanged += OnExtractionChanged;
        }

        private void Unhook()
        {
            if (_session != null)
            {
                _session.LeftRaid -= OnLeftRaid;
                _session.ExtractionChanged -= OnExtractionChanged;
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
