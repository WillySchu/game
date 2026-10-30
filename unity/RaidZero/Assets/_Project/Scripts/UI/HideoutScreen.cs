using System;
using System.Collections.Generic;
using FishNet.Managing;
using RaidZero.BackendApi;
using RaidZero.Net;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor; // UI Toolkit has a Cursor type too

namespace RaidZero.UI
{
    /// <summary>
    /// The Hideout: log in, sort your gear and stash, deploy, and see what a raid did. The gear and stash are the
    /// same inventory screen as in a raid (InventoryView) over HideoutInventorySource, so edits go to the backend
    /// with the same rules; nothing can be dropped here.
    ///
    /// Put it on any GameObject in the scene the game starts in. By default it shows whenever this game isn't in
    /// a raid (no client or server running on the scene's NetworkManager) and hides while it is, so it works in a
    /// single test scene as well as in a proper Hideout scene. Scene flow can turn that off (Show When Not In A
    /// Raid) and call <see cref="Show"/> and <see cref="Hide"/> itself.
    ///
    /// Deploy gets a ticket and connects, like the F1 overlay. Scene flow that loads the raid itself can listen
    /// to <see cref="DeployRequested"/>: when anything does, the screen leaves deploying to it.
    /// </summary>
    public sealed class HideoutScreen : MonoBehaviour
    {
        private const string NamePref = "raidzero.hideout_name";
        private const string ResultSeenPref = "raidzero.result_seen.";
        private const string Help = "drag to move   ·   R rotate while dragging   ·   Shift+drag split   ·   Ctrl+click quick move   ·   double-click open";

        [SerializeField]
        [Tooltip("The map Deploy asks for. Tickets only group players asking for the same map, and the server refuses a raid on a map it hasn't loaded.")]
        private string _mapId = "greybox_01";

        [SerializeField]
        [Tooltip("Show the Hideout whenever this game isn't in a raid, and hide it while it is. Turn off if scene flow shows and hides it.")]
        private bool _showWhenNotInARaid = true;

        // What the player carried in, by raid: so the result can name what they lost. Kept across scene loads.
        private static ItemNode _carriedIn;
        private static string _carriedInRaidId;

        private InventoryView _view;
        private ItemCatalog _catalog;
        private PlayerSession _session;
        private HideoutInventorySource _source;
        private InventoryModel _model;
        private NetworkManager _networkManager;

        private VisualElement _loginRow;
        private TextField _nameField;
        private VisualElement _playerRow;
        private Label _playerName;
        private Label _playerState;
        private Label _mapLabel;
        private Button _deployButton;
        private VisualElement _result;
        private Label _resultTitle;
        private Label _resultSentence;
        private VisualElement _resultLists;
        private string _resultRaidId;

        private bool _shown;
        private bool _wasInRaid;
        private bool _joinedRaid;
        private bool _dirty;
        private bool _busy;
        private string _waitingFor;

        /// <summary>Deploy was pressed (map ID). If anything listens, the screen doesn't deploy by itself.</summary>
        public event Action<string> DeployRequested;

        /// <summary>The screen deployed by itself and is connecting to the ticket's server.</summary>
        public event Action<DeployResult> Deployed;

        public bool IsShown => _shown;

        public string MapId => _mapId;

        public void Show()
        {
            if (_shown || _view == null)
            {
                return;
            }

            // The backend address may have changed while hidden (the F1 overlay), which gives a new session.
            if (_session != null && _session != Hideout.Session)
            {
                DisposeModel();
            }

            _shown = true;
            _view.Show();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EnsureModel();
            Refresh();
        }

        public void Hide()
        {
            if (!_shown)
            {
                return;
            }

            _shown = false;
            _view.Hide();
            _result.style.display = DisplayStyle.None;
        }

        private void Awake()
        {
            if (Application.isBatchMode)
            {
                // A dedicated server, or CI's scripted player: nobody to show a Hideout to.
                enabled = false;
                return;
            }

            _view = InventoryView.Create(transform, "Hideout UI", Help);
            if (_view == null)
            {
                enabled = false;
                return;
            }

            _view.Root.AddToClassList("hideout");
            BuildHeader();
            BuildFooter();
            BuildResult();
            RaidAuthenticator.JoinAnswered += OnJoinAnswered;
        }

        private void OnDestroy()
        {
            RaidAuthenticator.JoinAnswered -= OnJoinAnswered;
            DisposeModel();
        }

        private void Update()
        {
            if (_view == null)
            {
                return;
            }

            if (_networkManager == null)
            {
                _networkManager = FindAnyObjectByType<NetworkManager>();
            }

            var inRaid = _networkManager != null && (_networkManager.ClientManager.Started || _networkManager.IsServerStarted);
            if (_showWhenNotInARaid)
            {
                if (inRaid && _shown)
                {
                    Hide();
                }
                else if (!inRaid && !_shown)
                {
                    Show();
                }
            }

            _joinedRaid |= _networkManager != null && _networkManager.IsClientStarted;
            if (_wasInRaid && !inRaid)
            {
                // Back from a raid: wait for its result. A connection that never got in (turned away, or the
                // server wasn't there) has no result coming; the join answer already said why.
                if (_joinedRaid)
                {
                    WaitForResult(Hideout.LastTicket?.RaidId);
                }
                else
                {
                    Refresh();
                }

                _joinedRaid = false;
            }

            _wasInRaid = inRaid;
            if (!_shown)
            {
                return;
            }

            _view.HandleKeys(Keyboard.current);
            if (_dirty)
            {
                _dirty = false;
                Redraw();
            }

            _view.Tick();
        }

        // ---- Parts of the window ----

        private void BuildHeader()
        {
            _loginRow = new VisualElement().WithClass("hideout-row");
            _loginRow.Add(new Label("Name").WithClass("hideout-label"));
            _nameField = new TextField { value = PlayerPrefs.GetString(NamePref, "") }.WithClass("hideout-name-field");
            _nameField.maxLength = 32;
            _loginRow.Add(_nameField);
            _loginRow.Add(new Button(LogIn) { text = "Log in" }.WithClass("hideout-button"));
            _loginRow.Add(new Label("Dev login: the same name is the same player.").WithClass("hideout-quiet"));

            _playerRow = new VisualElement().WithClass("hideout-row");
            _playerName = new Label().WithClass("hideout-player");
            _playerState = new Label().WithClass("hideout-state");
            _playerRow.Add(_playerName);
            _playerRow.Add(_playerState);
            _playerRow.Add(new Button(Refresh) { text = "Refresh" }.WithClass("hideout-button"));
            _playerRow.Add(new Button(LogOut) { text = "Log out" }.WithClass("hideout-button"));

            _view.Header.Add(_loginRow);
            _view.Header.Add(_playerRow);
        }

        private void BuildFooter()
        {
            var row = new VisualElement().WithClass("hideout-row").WithClass("hideout-row--deploy");
            _mapLabel = new Label().WithClass("hideout-quiet");
            _deployButton = new Button(Deploy) { text = "Deploy" }.WithClass("hideout-button").WithClass("hideout-deploy");
            row.Add(_mapLabel);
            row.Add(_deployButton);
            _view.Footer.Add(row);
        }

        private void BuildResult()
        {
            _result = new VisualElement().WithClass("inv-split");
            _result.style.position = Position.Absolute;
            _result.style.left = 0;
            _result.style.top = 0;
            _result.style.right = 0;
            _result.style.bottom = 0;
            _result.style.display = DisplayStyle.None;

            var box = new VisualElement().WithClass("inv-split__box").WithClass("hideout-result");
            _resultTitle = new Label().WithClass("hideout-result__title");
            _resultSentence = new Label().WithClass("hideout-result__sentence");
            _resultLists = new VisualElement().WithClass("hideout-result__lists");
            var buttons = new VisualElement().WithClass("inv-split__buttons");
            buttons.Add(new Button(DismissResult) { text = "OK" }.WithClass("inv-split__ok"));
            box.Add(_resultTitle);
            box.Add(_resultSentence);
            box.Add(_resultLists);
            box.Add(buttons);
            _result.Add(box);
            _view.Root.Add(_result);
        }

        // ---- State ----

        private void EnsureModel()
        {
            if (_model != null)
            {
                return;
            }

            if (_catalog == null)
            {
                try
                {
                    _catalog = ServerConfig.LoadCatalog();
                }
                catch (Exception exception)
                {
                    Debug.LogError("[hideout] Couldn't load the item catalog: " + exception.Message);
                    enabled = false;
                    return;
                }
            }

            _session = Hideout.Session;
            _session.ProfileChanged += OnProfileChanged;
            _session.LoggedOut += OnLoggedOut;
            _source = new HideoutInventorySource(_session);
            _model = new InventoryModel(_catalog, _source);
            _model.Changed += OnModelChanged;
            _model.Refused += _view.ShowMessage;
            _view.SetModel(_model);
            _dirty = true;
        }

        private void DisposeModel()
        {
            if (_session != null)
            {
                _session.ProfileChanged -= OnProfileChanged;
                _session.LoggedOut -= OnLoggedOut;
                _session = null;
            }

            if (_model != null)
            {
                _model.Changed -= OnModelChanged;
                _model.Refused -= _view.ShowMessage;
                _model.Dispose();
                _model = null;
            }

            _source?.Dispose();
            _source = null;
            _view?.SetModel(null);
        }

        private void OnModelChanged() => _dirty = true;

        private void OnProfileChanged(PlayerProfile profile) => _dirty = true;

        private void OnLoggedOut()
        {
            _dirty = true;
            _view.ShowMessage("You've been logged out. Log in again.");
        }

        private void Redraw()
        {
            var profile = _session != null && _session.IsLoggedIn ? _session.Profile : null;
            _loginRow.style.display = profile == null ? DisplayStyle.Flex : DisplayStyle.None;
            _playerRow.style.display = profile == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (profile != null)
            {
                _playerName.text = profile.DisplayName;
                _playerState.text = _waitingFor != null ? "Waiting for the raid's result..."
                    : profile.IsInRaid ? "Your gear is in a raid. Deploy to rejoin it if you can, or wait for it to end."
                    : "";
            }

            _mapLabel.text = "Map: " + _mapId;
            _deployButton.text = _busy ? "Deploying..." : profile != null && profile.IsInRaid ? "Rejoin" : "Deploy";
            _deployButton.SetEnabled(profile != null && !_busy && _waitingFor == null);
            _view.Rebuild(profile == null ? "Log in to see your gear and stash." : "Loading your gear...");
        }

        // ---- Actions ----

        private async void LogIn()
        {
            var name = (_nameField.value ?? "").Trim();
            if (name.Length == 0 || _busy || _session == null)
            {
                _view.ShowMessage(name.Length == 0 ? "Type a name to log in with." : null);
                return;
            }

            _busy = true;
            try
            {
                var login = await _session.DevLoginAsync(name);
                if (!login.Succeeded)
                {
                    _view.ShowMessage("Couldn't log in: " + InventoryUiErrors.Describe(login.Error) + " (" + Hideout.BackendUrl + ")");
                    return;
                }

                PlayerPrefs.SetString(NamePref, name);
                PlayerPrefs.Save();
                Hideout.ForgetLastTicket();
                CheckLastRaid();
            }
            finally
            {
                _busy = false;
                _dirty = true;
            }
        }

        private void LogOut()
        {
            _session?.LogOut();
            Hideout.ForgetLastTicket();
            _dirty = true;
        }

        private async void Refresh()
        {
            if (_session == null)
            {
                return;
            }

            if (!_session.IsLoggedIn)
            {
                // A name from last time: log straight back in, as dev login is only a name.
                if (!string.IsNullOrWhiteSpace(_nameField.value) && !_busy)
                {
                    LogIn();
                }

                _dirty = true;
                return;
            }

            var refreshed = await _session.RefreshAsync();
            if (!refreshed.Succeeded)
            {
                _view.ShowMessage("Couldn't reach your profile: " + InventoryUiErrors.Describe(refreshed.Error));
            }
            else
            {
                CheckLastRaid();
            }

            _dirty = true;
        }

        private async void Deploy()
        {
            var profile = _session?.Profile;
            if (_busy || profile == null)
            {
                return;
            }

            if (DeployRequested != null)
            {
                RememberCarriedIn(profile, null);
                DeployRequested(_mapId);
                return;
            }

            if (_networkManager == null)
            {
                _view.ShowMessage("There's no NetworkManager in this scene to connect with.");
                return;
            }

            _busy = true;
            _dirty = true;
            try
            {
                var deployed = await Hideout.DeployAsync(_networkManager, _mapId);
                if (!deployed.Succeeded)
                {
                    _view.ShowMessage(DeployProblem(deployed));
                    return;
                }

                if (!deployed.Rejoined)
                {
                    RememberCarriedIn(profile, deployed.Ticket.RaidId);
                }

                _view.ShowMessage((deployed.Rejoined ? "Rejoining your raid at " : "Connecting to ") + deployed.Ticket.ServerHost + ":" + deployed.Ticket.ServerPort + "...");
                Deployed?.Invoke(deployed);
            }
            catch (Exception exception)
            {
                _view.ShowMessage("Deploy failed: " + exception.Message);
            }
            finally
            {
                _busy = false;
                _dirty = true;
            }
        }

        private static string DeployProblem(DeployResult deployed)
        {
            switch (deployed.Error)
            {
                case "already_in_raid":
                    return "Your gear is still in a raid this game can't rejoin. It comes back (or doesn't) when that raid ends.";
                case "no_server_available":
                    return "No raid server is free right now. Try again in a moment.";
                default:
                    return "Couldn't deploy: " + InventoryUiErrors.Describe(deployed.Error) + (string.IsNullOrEmpty(deployed.Message) ? "" : " (" + deployed.Message + ")");
            }
        }

        private void OnJoinAnswered(JoinRaidResultBroadcast answer)
        {
            if (!answer.Success && _view != null)
            {
                _view.ShowMessage("The raid server turned you away: " + (string.IsNullOrEmpty(answer.Message) ? answer.Error : answer.Message));
            }
        }

        // ---- Raid results ----

        private static void RememberCarriedIn(PlayerProfile profile, string raidId)
        {
            _carriedIn = Copy(profile.Character);
            _carriedInRaidId = raidId;
        }

        // After logging in or refreshing: a finished raid the player hasn't seen the result of.
        private async void CheckLastRaid()
        {
            var profile = _session?.Profile;
            if (profile == null || profile.IsInRaid || string.IsNullOrEmpty(profile.LastRaidId) || _waitingFor != null || _resultRaidId != null)
            {
                return;
            }

            if (PlayerPrefs.GetString(ResultSeenPref + profile.PlayerId, "") == profile.LastRaidId)
            {
                return;
            }

            var result = await _session.GetRaidResultAsync(profile.LastRaidId);
            if (result.Succeeded && result.Value.IsFinished)
            {
                ShowResult(result.Value);
            }
        }

        // Back from a raid: the server reports outcomes in the background, so the result can take a moment.
        private async void WaitForResult(string raidId)
        {
            if (_session == null || !_session.IsLoggedIn || string.IsNullOrEmpty(raidId) || _waitingFor != null)
            {
                return;
            }

            _waitingFor = raidId;
            _dirty = true;
            try
            {
                var result = await _session.WaitForRaidResultAsync(raidId, TimeSpan.FromSeconds(90));
                if (result.Succeeded)
                {
                    ShowResult(result.Value);
                }
                else
                {
                    _view.ShowMessage("The raid's result hasn't arrived yet: " + InventoryUiErrors.Describe(result.Error) + ". Refresh to check again.");
                }
            }
            finally
            {
                _waitingFor = null;
                _dirty = true;
            }
        }

        private void ShowResult(RaidOutcome outcome)
        {
            var carried = _carriedInRaidId == null || _carriedInRaidId == outcome.RaidId ? _carriedIn : null;
            var summary = RaidResultSummary.Build(outcome, _session?.Profile, _catalog, carried);
            _resultRaidId = outcome.RaidId;
            _resultTitle.text = summary.Title;
            _resultSentence.text = summary.Sentence;
            _resultLists.Clear();
            AddList("Brought home", summary.BroughtHome, null);
            AddList("Lost", summary.Lost, summary.Lost.Count == 0 && summary.LostCount > 0 ? summary.LostCount + " item(s)" : null);
            _result.style.display = DisplayStyle.Flex;
            _result.BringToFront();
        }

        private void AddList(string title, IReadOnlyList<string> lines, string instead)
        {
            if (lines.Count == 0 && instead == null)
            {
                return;
            }

            _resultLists.Add(new Label(title).WithClass("hideout-result__heading"));
            if (instead != null)
            {
                _resultLists.Add(new Label(instead).WithClass("hideout-result__line"));
            }

            foreach (var line in lines)
            {
                _resultLists.Add(new Label(line).WithClass("hideout-result__line"));
            }
        }

        private void DismissResult()
        {
            var profile = _session?.Profile;
            if (profile != null && _resultRaidId != null)
            {
                PlayerPrefs.SetString(ResultSeenPref + profile.PlayerId, _resultRaidId);
                PlayerPrefs.Save();
            }

            _resultRaidId = null;
            _result.style.display = DisplayStyle.None;
        }

        private static ItemNode Copy(ItemNode node)
        {
            if (node == null)
            {
                return null;
            }

            var copy = new ItemNode
            {
                Id = node.Id,
                DefId = node.DefId,
                Slot = node.Slot,
                X = node.X,
                Y = node.Y,
                Rotated = node.Rotated,
                Quantity = node.Quantity,
                Durability = node.Durability,
            };

            foreach (var child in node.Children ?? new List<ItemNode>())
            {
                copy.Children.Add(Copy(child));
            }

            return copy;
        }
    }
}
