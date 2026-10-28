using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using RaidZero.BackendApi;
using RaidZero.Raid;
using RaidZero.Shared.Inventory;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// The game server's raid: the connection to the backend, and the raid's inventory. Goes on the
    /// NetworkManager's GameObject, next to <see cref="RaidAuthenticator"/>, and does nothing unless this
    /// process is the server (dedicated, or the host in the editor).
    ///
    /// Gameplay code calls <see cref="PlayerDied"/>, <see cref="PlayerExtracted"/>, <see cref="Consume"/> and
    /// <see cref="EndRaidAsync"/>. World code listens to <see cref="PileCreated"/> and <see cref="PileRemoved"/>
    /// to spawn and despawn items on the ground, and registers bodies and piles in <see cref="ContainerRegistry"/>.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class RaidServer : MonoBehaviour
    {
        [Tooltip("How close, in metres, a player must be to a crate, body or pile to loot it.")]
        [SerializeField]
        private float _reachDistance = 2.5f;

        [Tooltip("A client sending this many malformed messages is disconnected.")]
        [SerializeField]
        private int _violationsBeforeKick = 5;

        private readonly Dictionary<NetworkConnection, string> _playerOf = new Dictionary<NetworkConnection, string>();
        private readonly Dictionary<string, NetworkConnection> _connectionOf = new Dictionary<string, NetworkConnection>(StringComparer.Ordinal);
        private readonly Dictionary<NetworkConnection, HostOutput> _welcome = new Dictionary<NetworkConnection, HostOutput>();
        private readonly Dictionary<string, int> _violations = new Dictionary<string, int>(StringComparer.Ordinal);

        private NetworkManager _networkManager;
        private BackendClient _backend;
        private RaidInventoryHost _host;

        /// <summary>A pile appeared on the ground: (pile ID, the dropping player's connection). Spawn a pickup at their feet.</summary>
        public event Action<string, NetworkConnection> PileCreated;

        /// <summary>A pile on the ground is empty and gone. Despawn its pickup.</summary>
        public event Action<string> PileRemoved;

        public bool IsRunning => _host != null;

        /// <summary>The raid's inventory, for gameplay that needs to read it (what's in a player's primary slot).</summary>
        public RaidInventoryService Inventory => _host?.Service;

        /// <summary>The player's ID, once they've joined. Null for connections that haven't.</summary>
        public string PlayerIdOf(NetworkConnection connection) =>
            connection != null && _playerOf.TryGetValue(connection, out var playerId) ? playerId : null;

        /// <summary>The ID of the player's character container, which is also their body's container ID once they die.</summary>
        public string CharacterIdOf(NetworkConnection connection) => Inventory?.CharacterOf(PlayerIdOf(connection));

        // ---- Called by RaidAuthenticator ----

        /// <summary>Redeems the ticket a connecting player presented. On success they're in the raid's inventory.</summary>
        internal async Task<(bool Success, string Error, string Message)> JoinAsync(NetworkConnection connection, string raidId, string joinToken)
        {
            if (!IsRunning)
            {
                return (false, "server_not_ready", "This server isn't running a raid.");
            }

            var joined = await _backend.JoinAsync(raidId, joinToken);
            if (!joined.Succeeded)
            {
                return (false, joined.Error, joined.Message);
            }

            if (!IsRunning || !connection.IsActive)
            {
                // They left while we waited. Their gear is in the raid now; the same ticket gets them back in.
                return (false, "disconnected", "The connection closed during the join.");
            }

            if (_connectionOf.TryGetValue(joined.PlayerId, out var existing) && existing != connection && existing.IsActive)
            {
                return (false, "already_connected", "This player is already connected to the raid.");
            }

            _playerOf[connection] = joined.PlayerId;
            _connectionOf[joined.PlayerId] = connection;
            connection.CustomData = joined.PlayerId;

            // Their first views can only be sent once Fish-Net has marked them authenticated.
            _welcome[connection] = _host.Join(joined.PlayerId, joined.Loadout);
            return (true, null, null);
        }

        internal void OnAuthenticated(NetworkConnection connection)
        {
            if (_welcome.TryGetValue(connection, out var output))
            {
                _welcome.Remove(connection);
                Dispatch(output, connection);
            }
        }

        // ---- Called by gameplay ----

        /// <summary>
        /// The player died. Their gear stays as a body: register the body object in ContainerRegistry under
        /// <see cref="CharacterIdOf"/> (before this call, while the ID is still theirs) so others can loot it.
        /// </summary>
        public void PlayerDied(NetworkConnection connection)
        {
            var playerId = PlayerIdOf(connection);
            if (playerId == null || !Inventory.IsActive(playerId))
            {
                return;
            }

            _host.Kill(playerId);
            _backend.ReportDied(playerId);
        }

        /// <summary>The player finished extracting. What they carry goes to the backend, and they leave the raid's inventory.</summary>
        public void PlayerExtracted(NetworkConnection connection, string extractPoint = null)
        {
            var playerId = PlayerIdOf(connection);
            if (playerId == null || !Inventory.IsActive(playerId))
            {
                return;
            }

            _backend.ReportExtracted(playerId, _host.Extract(playerId), extractPoint);
        }

        /// <summary>Uses up rounds or a medkit the player carries. False, changing nothing, if they don't have that many.</summary>
        public bool Consume(NetworkConnection connection, string itemId, int quantity)
        {
            var playerId = PlayerIdOf(connection);
            return playerId != null && _host.Consume(playerId, itemId, quantity, Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>
        /// The raid is over (the timer ran out, or everyone left). Anyone still inside dies. Completes once the
        /// backend has every report; after that the server process can exit.
        /// </summary>
        public Task EndRaidAsync() => IsRunning ? _backend.EndRaidAsync() : Task.CompletedTask;

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
            if (IsRunning)
            {
                Dispatch(_host.Tick(Time.realtimeSinceStartupAsDouble), null);
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

            // Their character stays in the raid where they left it; the raid's close counts them as dead.
            _welcome.Remove(connection);
            if (_playerOf.TryGetValue(connection, out var playerId))
            {
                _playerOf.Remove(connection);
                if (_connectionOf.TryGetValue(playerId, out var current) && current == connection)
                {
                    _connectionOf.Remove(playerId);
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
                _backend = new BackendClient(settings, catalog.Version, log: new UnityBackendLog());
                _backend.RaidAborted += OnRaidAborted;
                _backend.ReportRejected += rejection => Debug.LogError("[raid] The backend refused a report: " + rejection);
                _backend.Start();

                var inventory = new RaidInventoryService(catalog);
                foreach (var container in FindObjectsByType<LootContainer>(FindObjectsSortMode.None))
                {
                    inventory.AddContainer(container.BuildContents(catalog));
                }

                _host = new RaidInventoryHost(inventory) { CanReach = CanReach };
                _host.ProtocolViolation += OnProtocolViolation;
                _networkManager.ServerManager.RegisterBroadcast<InventoryToServerBroadcast>(OnInventoryBroadcast);
                Debug.Log("[raid] Server ready: " + settings + ", catalog " + catalog.Version + ".");
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
                // through gameplay (EndRaidAsync) before it exits.
                if (Application.isEditor && _backend.RaidId != null)
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
            _playerOf.Clear();
            _connectionOf.Clear();
            _welcome.Clear();
            _violations.Clear();
        }

        // ---- Messages ----

        private void OnInventoryBroadcast(NetworkConnection connection, InventoryToServerBroadcast message, Channel channel)
        {
            var playerId = PlayerIdOf(connection);
            if (playerId != null && IsRunning)
            {
                Dispatch(_host.Receive(playerId, message.Data, Time.realtimeSinceStartupAsDouble), connection);
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
            foreach (var connection in new List<NetworkConnection>(_playerOf.Keys))
            {
                connection.Disconnect(false);
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
