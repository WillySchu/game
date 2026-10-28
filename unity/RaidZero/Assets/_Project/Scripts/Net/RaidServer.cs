using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using RaidZero.BackendApi;
using RaidZero.Raid;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// The game server's raid: the connection to the backend, the raid's inventory, and its life from the
    /// first join to the close. Goes on the NetworkManager's GameObject, next to <see cref="RaidAuthenticator"/>,
    /// and does nothing unless this process is the server (dedicated, or the host in the editor).
    ///
    /// The raid waits until someone joins, then runs its clock. It ends when the clock runs out, when everyone
    /// who joined has extracted or died, or when nobody inside has been connected for a while. Then anyone
    /// still inside is missing in action, the backend gets the close, and a dedicated server exits so its
    /// supervisor can start a fresh one.
    ///
    /// Gameplay code calls <see cref="PlayerDied"/>, <see cref="Consume"/> and <see cref="InterruptExtraction"/>;
    /// <see cref="ExtractionZone"/>s in the level handle extraction. World code listens to <see cref="PileCreated"/>
    /// and <see cref="PileRemoved"/> to spawn and despawn items on the ground, and registers bodies and piles in
    /// <see cref="ContainerRegistry"/>.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class RaidServer : MonoBehaviour
    {
        [Tooltip("The map this scene is, as tickets name it. The backend refuses to send players here for a raid on another map.")]
        [SerializeField]
        private string _mapId = "greybox_01";

        [Tooltip("How close, in metres, a player must be to a crate, body or pile to loot it.")]
        [SerializeField]
        private float _reachDistance = 3f;

        [Tooltip("A client sending this many malformed messages is disconnected.")]
        [SerializeField]
        private int _violationsBeforeKick = 5;

        [Header("Raid")]
        [Tooltip("How long a raid runs from its first join, in minutes. --raid-minutes on the command line overrides it.")]
        [SerializeField]
        private float _raidMinutes = 25f;

        [Tooltip("A raid nobody inside is connected to ends after this many seconds.")]
        [SerializeField]
        private float _emptyRaidSeconds = 180f;

        [Tooltip("Seconds between telling a player they're out and disconnecting them, so the message arrives and the client can show it.")]
        [SerializeField]
        private float _leaveDelaySeconds = 3f;

        [Tooltip("How long a dedicated server waits for the backend to take the results before exiting anyway. Whatever it hasn't taken stays in the outbox for the next run.")]
        [SerializeField]
        private float _closeTimeoutSeconds = 60f;

        private readonly Dictionary<NetworkConnection, string> _playerOf = new Dictionary<NetworkConnection, string>();
        private readonly Dictionary<string, NetworkConnection> _connectionOf = new Dictionary<string, NetworkConnection>(StringComparer.Ordinal);
        private readonly Dictionary<NetworkConnection, HostOutput> _welcome = new Dictionary<NetworkConnection, HostOutput>();
        private readonly Dictionary<string, int> _violations = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<NetworkConnection, double> _disconnectAt = new Dictionary<NetworkConnection, double>();

        private NetworkManager _networkManager;
        private BackendClient _backend;
        private RaidInventoryHost _host;
        private RaidLifecycle _lifecycle;
        private ExtractionTracker _extraction;
        private Task _closing;
        private double _closingSince;

        /// <summary>A pile appeared on the ground: (pile ID, the dropping player's connection). Spawn a pickup at their feet.</summary>
        public event Action<string, NetworkConnection> PileCreated;

        /// <summary>A pile on the ground is empty and gone. Despawn its pickup.</summary>
        public event Action<string> PileRemoved;

        /// <summary>The raid moved to another phase. On Ending, gameplay can stop AI and spawning.</summary>
        public event Action<RaidPhase> PhaseChanged;

        public bool IsRunning => _host != null;

        public RaidPhase Phase => _lifecycle?.Phase ?? RaidPhase.Waiting;

        /// <summary>Seconds left on the raid's clock.</summary>
        public double SecondsRemaining => _lifecycle?.SecondsRemaining(Now) ?? 0;

        /// <summary>The raid's inventory, for gameplay that needs to read it (what's in a player's primary slot).</summary>
        public RaidInventoryService Inventory => _host?.Service;

        /// <summary>The player's ID, once they've joined. Null for connections that haven't.</summary>
        public string PlayerIdOf(NetworkConnection connection) =>
            connection != null && _playerOf.TryGetValue(connection, out var playerId) ? playerId : null;

        /// <summary>The ID of the player's character container, which is also their body's container ID once they die.</summary>
        public string CharacterIdOf(NetworkConnection connection) => Inventory?.CharacterOf(PlayerIdOf(connection));

        private static double Now => Time.realtimeSinceStartupAsDouble;

        // ---- Called by RaidAuthenticator ----

        /// <summary>Redeems the ticket a connecting player presented. On success they're in the raid's inventory.</summary>
        internal async Task<(bool Success, string Error, string Message)> JoinAsync(NetworkConnection connection, string raidId, string joinToken)
        {
            var lifecycle = _lifecycle;
            if (!IsRunning)
            {
                return (false, "server_not_ready", "This server isn't running a raid.");
            }

            if (!lifecycle.TryBeginJoin())
            {
                return (false, "raid_ending", "This raid is over and takes no one new.");
            }

            try
            {
                var joined = await _backend.JoinAsync(raidId, joinToken);
                if (!joined.Succeeded)
                {
                    return (false, joined.Error, joined.Message);
                }

                if (!IsRunning || lifecycle != _lifecycle || !connection.IsActive)
                {
                    // They left while we waited. Their gear is in the raid now; the same ticket gets them back in.
                    lifecycle.PlayerJoined(joined.PlayerId, connected: false, Now);
                    return (false, "disconnected", "The connection closed during the join.");
                }

                if (_connectionOf.TryGetValue(joined.PlayerId, out var existing) && existing != connection && existing.IsActive)
                {
                    return (false, "already_connected", "This player is already connected to the raid.");
                }

                _playerOf[connection] = joined.PlayerId;
                _connectionOf[joined.PlayerId] = connection;
                connection.CustomData = joined.PlayerId;

                var wasWaiting = lifecycle.Phase == RaidPhase.Waiting;
                lifecycle.PlayerJoined(joined.PlayerId, connected: true, Now);
                if (wasWaiting)
                {
                    Debug.Log("[raid] Raid " + _backend.RaidId + " started: " + lifecycle.Duration.TotalMinutes + " minutes on the clock.");
                    PhaseChanged?.Invoke(lifecycle.Phase);
                }

                // Their first views can only be sent once Fish-Net has marked them authenticated.
                _welcome[connection] = _host.Join(joined.PlayerId, joined.Loadout);
                return (true, null, null);
            }
            finally
            {
                lifecycle.EndJoin();
            }
        }

        internal void OnAuthenticated(NetworkConnection connection)
        {
            if (_welcome.TryGetValue(connection, out var output))
            {
                _welcome.Remove(connection);
                Dispatch(output, connection);
            }

            SendState(connection);
        }

        // ---- Called by gameplay ----

        /// <summary>
        /// The player died. Their gear stays as a body: register the body object in ContainerRegistry under
        /// <see cref="CharacterIdOf"/> (before this call, while the ID is still theirs) so others can loot it.
        /// The body must be its own object: the player is disconnected shortly after, and Fish-Net despawns
        /// whatever their connection owns.
        /// </summary>
        public void PlayerDied(NetworkConnection connection)
        {
            var playerId = PlayerIdOf(connection);
            if (!IsInside(playerId))
            {
                return;
            }

            _host.Kill(playerId);
            _backend.ReportDied(playerId);
            Leave(playerId, connection, RaidExit.Died);
        }

        /// <summary>
        /// Extracts the player at once. <see cref="ExtractionZone"/>s call this through the countdown; call it
        /// directly only for extractions that work differently (a scripted exit).
        /// </summary>
        public void PlayerExtracted(NetworkConnection connection, string extractPoint = null)
        {
            var playerId = PlayerIdOf(connection);
            if (!IsInside(playerId))
            {
                return;
            }

            _backend.ReportExtracted(playerId, _host.Extract(playerId), extractPoint);
            Leave(playerId, connection, RaidExit.Extracted);
        }

        /// <summary>The player took damage: if they're extracting, their countdown starts over.</summary>
        public void InterruptExtraction(NetworkConnection connection)
        {
            var playerId = PlayerIdOf(connection);
            if (playerId != null && _extraction != null && _extraction.Interrupt(playerId, Now))
            {
                SendExtraction(connection, _extraction.ZoneOf(playerId), _extraction.SecondsLeft(playerId, Now) ?? 0);
            }
        }

        /// <summary>Uses up rounds or a medkit the player carries. False, changing nothing, if they don't have that many.</summary>
        public bool Consume(NetworkConnection connection, string itemId, int quantity)
        {
            var playerId = PlayerIdOf(connection);
            return IsInside(playerId) && _host.Consume(playerId, itemId, quantity, Now);
        }

        /// <summary>Ends the raid now, as if the clock had run out: anyone still inside is missing in action.</summary>
        public void EndRaid()
        {
            if (IsRunning && _lifecycle.Phase == RaidPhase.InProgress)
            {
                _lifecycle.End(RaidEndReason.TimeUp, Now);
                BeginEnding();
            }
        }

        // ---- Lifecycle ----

        private void Start()
        {
            _networkManager = GetComponent<NetworkManager>();
            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            if (_networkManager.IsServerStarted)
            {
                StartRaid();
            }
        }

        private void OnDestroy()
        {
            if (_networkManager != null)
            {
                _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
                _networkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            }

            StopRaid();
        }

        private void Update()
        {
            if (!IsRunning)
            {
                return;
            }

            var now = Now;
            Dispatch(_host.Tick(now), null);
            if (_lifecycle.Phase == RaidPhase.InProgress)
            {
                UpdateExtractions(now);
            }

            if (_lifecycle.Tick(now))
            {
                BeginEnding();
            }

            DisconnectThoseDue(now);
            if (_lifecycle.Phase == RaidPhase.Ending && _disconnectAt.Count == 0
                && (_closing.IsCompleted || now - _closingSince >= _closeTimeoutSeconds))
            {
                FinishClose();
            }
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                StartRaid();
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                StopRaid();
            }
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped)
            {
                return;
            }

            // Their character stays in the raid where they left it, and they can come back with the same ticket.
            // If the raid ends first, they're missing in action.
            _welcome.Remove(connection);
            _disconnectAt.Remove(connection);
            if (_playerOf.TryGetValue(connection, out var playerId))
            {
                _playerOf.Remove(connection);
                if (_connectionOf.TryGetValue(playerId, out var current) && current == connection)
                {
                    _connectionOf.Remove(playerId);
                    _lifecycle?.PlayerDisconnected(playerId);
                    _extraction?.Forget(playerId);
                }
            }
        }

        private void StartRaid()
        {
            if (IsRunning)
            {
                return;
            }

            try
            {
                var catalog = ServerConfig.LoadCatalog();
                var settings = ServerConfig.ResolveBackend();
                var minutes = ServerConfig.FloatArgument("--raid-minutes") ?? _raidMinutes;

                _backend = new BackendClient(settings, catalog.Version, new BackendClientOptions { MapId = _mapId }, log: new UnityBackendLog());
                _backend.RaidAborted += OnRaidAborted;
                _backend.ReportRejected += rejection => Debug.LogError("[raid] The backend refused a report: " + rejection);
                _backend.Start();

                var inventory = new RaidInventoryService(catalog);
                foreach (var container in FindObjectsByType<LootContainer>(FindObjectsSortMode.None))
                {
                    inventory.AddContainer(container.BuildContents(catalog));
                }

                _lifecycle = new RaidLifecycle(new RaidLifecycleOptions
                {
                    Duration = TimeSpan.FromMinutes(minutes),
                    EmptyGrace = TimeSpan.FromSeconds(_emptyRaidSeconds),
                });
                _extraction = new ExtractionTracker();
                _host = new RaidInventoryHost(inventory) { CanReach = CanReach };
                _host.ProtocolViolation += OnProtocolViolation;
                _networkManager.ServerManager.RegisterBroadcast<InventoryToServerBroadcast>(OnInventoryBroadcast);
                Debug.Log("[raid] Server ready: " + settings + ", map " + _mapId + ", catalog " + catalog.Version + ", " + minutes + "-minute raids, "
                    + ExtractionZone.All.Count + " extraction zone(s).");
                if (ExtractionZone.All.Count == 0)
                {
                    Debug.LogWarning("[raid] This level has no ExtractionZone, so the only way out is the clock running out.");
                }
            }
            catch (Exception exception)
            {
                // Without the backend or the catalog nobody can join, so say so loudly rather than limp on.
                Debug.LogError("[raid] The raid server couldn't start: " + exception.Message);
                StopRaid();
                _networkManager.ServerManager.StopConnection(true);
            }
        }

        private void StopRaid()
        {
            if (_networkManager != null)
            {
                _networkManager.ServerManager.UnregisterBroadcast<InventoryToServerBroadcast>(OnInventoryBroadcast);
            }

            if (_backend != null)
            {
                // Pressing Stop in the editor ends the raid properly, so the dev server is free again at once.
                // Dev players are throwaway, so losing their gear doesn't matter. A real server ends raids
                // through the lifecycle before it exits.
                if (Application.isEditor && _backend.RaidId != null && Phase != RaidPhase.Closed)
                {
                    try
                    {
                        _backend.EndRaidAsync().Wait(TimeSpan.FromSeconds(3));
                    }
                    catch (AggregateException)
                    {
                    }
                }

                _backend.Dispose();
            }

            _backend = null;
            _host = null;
            _lifecycle = null;
            _extraction = null;
            _closing = null;
            _playerOf.Clear();
            _connectionOf.Clear();
            _welcome.Clear();
            _violations.Clear();
            _disconnectAt.Clear();
        }

        private void UpdateExtractions(double now)
        {
            List<(NetworkConnection Connection, string Point)> done = null;
            foreach (var pair in _connectionOf)
            {
                var playerId = pair.Key;
                var connection = pair.Value;
                if (!_lifecycle.IsInside(playerId) || !connection.IsActive || connection.FirstObject == null)
                {
                    continue;
                }

                var zone = ExtractionZone.At(connection.FirstObject.transform.position);
                switch (_extraction.Update(playerId, zone != null ? zone.PointName : null, zone != null ? zone.Seconds : 0, now))
                {
                    case ExtractionChange.Started:
                        SendExtraction(connection, zone.PointName, zone.Seconds);
                        break;
                    case ExtractionChange.Cancelled:
                        SendExtraction(connection, null, 0);
                        break;
                    case ExtractionChange.Completed:
                        (done = done ?? new List<(NetworkConnection, string)>()).Add((connection, zone.PointName));
                        break;
                }
            }

            if (done != null)
            {
                foreach (var (connection, point) in done)
                {
                    PlayerExtracted(connection, point);
                }
            }
        }

        private void BeginEnding()
        {
            var reason = _lifecycle.EndReason;
            Debug.Log("[raid] Raid " + (_backend.RaidId ?? "(nobody joined)") + " is over: " + reason + ". Still inside: "
                + _lifecycle.PlayersInside.Count() + ".");

            var exit = reason == RaidEndReason.Aborted ? RaidExit.Aborted : RaidExit.MissingInAction;
            foreach (var playerId in _lifecycle.PlayersInside.ToList())
            {
                if (_connectionOf.TryGetValue(playerId, out var connection) && connection.IsActive)
                {
                    Tell(connection, exit);
                }
            }

            _extraction = new ExtractionTracker();
            foreach (var connection in _playerOf.Keys)
            {
                SendState(connection);
            }

            PhaseChanged?.Invoke(RaidPhase.Ending);

            // An aborted raid is already settled on the backend; anything else needs the close. Either way the
            // backend counts whoever is still inside as dead, so there's nothing to report for them.
            _closing = reason == RaidEndReason.Aborted ? Task.CompletedTask : _backend.EndRaidAsync();
            _closingSince = Now;
        }

        private void FinishClose()
        {
            if (!_closing.IsCompleted)
            {
                Debug.LogWarning("[raid] The backend hasn't taken every result yet. They stay in the outbox; the next run on this machine sends them.");
            }
            else if (_closing.IsFaulted)
            {
                Debug.LogError("[raid] Closing the raid failed: " + _closing.Exception?.GetBaseException().Message);
            }

            _lifecycle.MarkClosed();
            PhaseChanged?.Invoke(RaidPhase.Closed);

            if (Application.isBatchMode && !Application.isEditor)
            {
                // One raid per process: exit, and the supervisor starts a fresh server for the next one.
                Debug.Log("[raid] Raid closed. Exiting.");
                Application.Quit(0);
            }
            else
            {
                Debug.Log("[raid] Raid closed, so the server has stopped. Start it again for a new raid.");
                _networkManager.ServerManager.StopConnection(true);
            }
        }

        private bool IsInside(string playerId) =>
            playerId != null && IsRunning && _lifecycle.Phase == RaidPhase.InProgress && _lifecycle.IsInside(playerId);

        private void Leave(string playerId, NetworkConnection connection, RaidExit exit)
        {
            _lifecycle.PlayerLeft(playerId);
            _extraction.Forget(playerId);
            if (connection != null && connection.IsActive)
            {
                Tell(connection, exit);
            }
        }

        /// <summary>Tells the player how they left, then disconnects them after a moment.</summary>
        private void Tell(NetworkConnection connection, RaidExit exit)
        {
            _networkManager.ServerManager.Broadcast(connection, new LeftRaidBroadcast { Exit = exit });
            _disconnectAt[connection] = Now + _leaveDelaySeconds;
        }

        private void DisconnectThoseDue(double now)
        {
            if (_disconnectAt.Count == 0)
            {
                return;
            }

            foreach (var pair in _disconnectAt.Where(p => p.Value <= now).ToList())
            {
                _disconnectAt.Remove(pair.Key);
                if (pair.Key.IsActive)
                {
                    pair.Key.Disconnect(false);
                }
            }
        }

        private void SendState(NetworkConnection connection)
        {
            if (!IsRunning || connection == null || !connection.IsActive || !connection.IsAuthenticated)
            {
                return;
            }

            _networkManager.ServerManager.Broadcast(connection, new RaidStateBroadcast
            {
                Phase = _lifecycle.Phase,
                SecondsRemaining = (float)_lifecycle.SecondsRemaining(Now),
                Duration = (float)_lifecycle.Duration.TotalSeconds,
            });
        }

        private void SendExtraction(NetworkConnection connection, string point, double secondsLeft)
        {
            if (connection.IsActive)
            {
                _networkManager.ServerManager.Broadcast(connection, new ExtractionBroadcast { Point = point, SecondsLeft = (float)secondsLeft });
            }
        }

        // ---- Messages ----

        private void OnInventoryBroadcast(NetworkConnection connection, InventoryToServerBroadcast message, Channel channel)
        {
            var playerId = PlayerIdOf(connection);
            if (playerId != null && IsRunning)
            {
                Dispatch(_host.Receive(playerId, message.Data, Now), connection);
            }
        }

        private void Dispatch(HostOutput output, NetworkConnection sender)
        {
            foreach (var message in output.Messages)
            {
                if (_connectionOf.TryGetValue(message.PlayerId, out var connection) && connection.IsActive)
                {
                    _networkManager.ServerManager.Broadcast(connection, new InventoryToClientBroadcast { Data = message.Data });
                }
            }

            foreach (var pileId in output.CreatedRoots)
            {
                PileCreated?.Invoke(pileId, sender);
            }

            foreach (var pileId in output.RemovedRoots)
            {
                PileRemoved?.Invoke(pileId);
            }
        }

        private bool CanReach(string playerId, string containerId)
        {
            if (!_connectionOf.TryGetValue(playerId, out var connection) || connection.FirstObject == null)
            {
                return false;
            }

            return ContainerRegistry.TryGetPosition(containerId, out var position)
                && Vector3.Distance(connection.FirstObject.transform.position, position) <= _reachDistance;
        }

        private void OnProtocolViolation(string playerId, string reason)
        {
            _violations.TryGetValue(playerId, out var count);
            _violations[playerId] = ++count;
            Debug.LogWarning("[raid] Player " + playerId + " sent a malformed inventory message (" + count + "): " + reason);
            if (count >= _violationsBeforeKick && _connectionOf.TryGetValue(playerId, out var connection))
            {
                connection.Disconnect(false);
            }
        }

        private void OnRaidAborted(RaidAbortedEvent aborted)
        {
            // The backend has already given everyone their gear back; nothing here counts any more.
            Debug.LogError("[raid] The backend aborted raid " + aborted.RaidId + ": " + aborted.Message);
            if (IsRunning && (_lifecycle.Phase == RaidPhase.Waiting || _lifecycle.Phase == RaidPhase.InProgress))
            {
                _lifecycle.End(RaidEndReason.Aborted, Now);
                BeginEnding();
            }
        }

        private sealed class UnityBackendLog : IBackendLog
        {
            public void Write(BackendLogLevel level, string message)
            {
                switch (level)
                {
                    case BackendLogLevel.Error:
                        Debug.LogError("[backend] " + message);
                        break;
                    case BackendLogLevel.Warning:
                        Debug.LogWarning("[backend] " + message);
                        break;
                    default:
                        Debug.Log("[backend] " + message);
                        break;
                }
            }
        }
    }
}
