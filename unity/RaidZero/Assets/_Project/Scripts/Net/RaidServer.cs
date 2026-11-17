using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using RaidZero.BackendApi;
using RaidZero.Items;
using RaidZero.Raid;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RaidZero.Net
{
    /// <summary>What <see cref="RaidServer.Hit"/> did: nothing (nobody there to hurt), a hit, or a kill.</summary>
    public enum HitOutcome
    {
        None,
        Hit,
        Killed,
    }

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
    /// Gameplay code calls <see cref="SpawnAI"/>, <see cref="Hit(string, string, string, BodyPart)"/>, <see cref="PlayerDied"/>, <see cref="LoadedRounds"/>, <see cref="FireRound"/>, <see cref="Reload"/>,
    /// <see cref="Consume"/> and <see cref="InterruptExtraction"/> (through <see cref="Instance"/>);
    /// <see cref="ExtractionZone"/>s in the level handle extraction. Items on the ground are this class's too:
    /// it rolls the level's <see cref="LooseLootSpot"/>s at raid start and spawns a <see cref="GroundItem"/> for
    /// each pile, from loose loot or a player's drop, and despawns it when the pile is emptied.
    /// <see cref="PileCreated"/> and <see cref="PileRemoved"/> are there for anything else that cares.
    ///
    /// The raid starts once the server is listening and the map scene has loaded, whichever comes last: a
    /// dedicated server starts listening in the core scene, before the map's crates and loot spots exist.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public sealed class RaidServer : MonoBehaviour
    {
        /// <summary>The map's scene. Currently always SampleScene; eventually the scene that goes with the map ID.</summary>
        private const string MapScene = "SampleScene";

        /// <summary>How far in front of a player what they drop lands, in metres.</summary>
        private const float DropDistance = 0.75f;

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

        [Tooltip("The seed for this raid's loot. 0 picks a new one each raid; the log names it, so setting it here (or --loot-seed on the command line) rolls that raid's loot again.")]
        [SerializeField]
        private int _lootSeed = 0;

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
        private readonly Dictionary<string, ExtractionZone> _testZones = new Dictionary<string, ExtractionZone>(StringComparer.Ordinal);

        private NetworkManager _networkManager;
        private BackendClient _backend;
        private RaidInventoryHost _host;
        private RaidHealth _health;
        private readonly Dictionary<string, NetworkObject> _ai = new Dictionary<string, NetworkObject>(StringComparer.Ordinal);
        private RaidLifecycle _lifecycle;
        private ExtractionTracker _extraction;
        private Task _closing;
        private double _closingSince;
        private float _restartAt;
        private bool _testExtractAtSpawn;

        private readonly Dictionary<string, GroundItem> _groundItems = new Dictionary<string, GroundItem>(StringComparer.Ordinal);
        private NetworkObject _groundItemPrefab;
        private NetworkObject _bodyPrefab;

        /// <summary>A player dropped something, which now lies in a new pile: (pile ID, their connection). Its GroundItem is already spawned.</summary>
        public event Action<string, NetworkConnection> PileCreated;

        /// <summary>A pile on the ground was emptied and is gone. Its GroundItem is already despawned.</summary>
        public event Action<string> PileRemoved;

        /// <summary>The raid moved to another phase. On Ending, gameplay can stop AI and spawning.</summary>
        public event Action<RaidPhase> PhaseChanged;

        /// <summary>
        /// The raid server, for gameplay code on the server (the player controller firing). Use it only where
        /// the server is running (IsServerStarted): an editor client may have one that isn't.
        /// </summary>
        public static RaidServer Instance { get; private set; }

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
                    // Their game crashed or lost its connection and came back before the old one timed out. The
                    // ticket proves it's them, so the new connection takes over and the old one goes (its
                    // character with it; they spawn afresh, as on any rejoin).
                    Debug.Log("[raid] " + joined.PlayerId + " came back on a new connection; closing the old one.");
                    _playerOf.Remove(existing);
                    _welcome.Remove(existing);
                    _disconnectAt.Remove(existing);
                    _extraction?.Forget(joined.PlayerId);
                    existing.Disconnect(true);
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
            SendHealth(connection); // full, or as hurt as they were when they left, on a rejoin
        }

        // ---- Called by gameplay ----

        /// <summary>
        /// The player died. Their gear stays where they fell as a <see cref="Body"/> anyone can search, the
        /// backend is told, and they're sent home. The body is its own object: the player is disconnected
        /// shortly after, and Fish-Net despawns whatever their connection owns.
        /// </summary>
        public void PlayerDied(NetworkConnection connection)
        {
            var playerId = PlayerIdOf(connection);
            if (!IsInside(playerId))
            {
                return;
            }

            SpawnBody(Inventory.CharacterOf(playerId), connection.FirstObject != null ? connection.FirstObject.transform : null);
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

        // ---- Actors: players and AI, by ID ----
        //
        // A player's ID is the one the backend gave them; an AI's is its own ("ai-" and a UUID), from SpawnAI.
        // The raid inventory and health work the same for both. The calls taking a NetworkConnection are the
        // player controller's: they look up the player's ID and call these.

        /// <summary>The player's ID for a connection (a player in this raid), or null.</summary>
        public string ActorIdOf(NetworkConnection connection) => PlayerIdOf(connection);

        /// <summary>Whether this is a live AI in the raid.</summary>
        public bool IsAI(string actorId) => actorId != null && _ai.ContainsKey(actorId);

        /// <summary>
        /// A round hit someone, a player or an AI: the shooter's gun calls this on the server when its shot's ray
        /// hits a hitbox. The damage comes from the round's AmmoStats (<paramref name="ammoDefId"/> is what
        /// FireRound returned), scaled by where it landed, with the victim's armor (their armor slot) taking its
        /// share where it covers, by <see cref="DamageRules"/>. The armor wears and the victim's health drops. A
        /// player's client is told, an extraction countdown starts over, and at no health they die
        /// (<see cref="PlayerDied"/>); an AI at no health dies too, leaving its body. Nothing happens to anyone
        /// who isn't active in the raid, or when someone hits themselves.
        /// </summary>
        /// <param name="shooterId">Who fired, or null for nobody in particular (a dev shot, later the world).</param>
        public HitOutcome Hit(string victimId, string shooterId, string ammoDefId, BodyPart part)
        {
            if (!IsActive(victimId) || _health == null || (shooterId != null && shooterId == victimId))
            {
                return HitOutcome.None;
            }

            var ammo = StatsOf<AmmoStats>(ammoDefId);
            if (ammo == null)
            {
                Debug.LogWarning("[raid] A hit with '" + ammoDefId + "', which has no ammo stats, did nothing.");
                return HitOutcome.None;
            }

            // The armor where it landed, if it covers there and has its stats filled in.
            ItemNode armor = null;
            ArmorStats armorStats = null;
            ArmorHit? armorHit = null;
            if (DamageRules.ArmorCovers(part))
            {
                armor = Inventory.ArmorOf(victimId);
                armorStats = StatsOf<ArmorStats>(armor?.DefId);
                if (armor?.Durability != null && armorStats != null && armorStats.durability > 0)
                {
                    armorHit = new ArmorHit(armorStats.protection, (float)armor.Durability.Value * armorStats.durability, armorStats.durability);
                }
            }

            var damage = DamageRules.Resolve(ammo.damage, ammo.penetration, part, armorHit);
            if (damage.ArmorWear > 0 && armorHit != null)
            {
                _host.Wear(victimId, armor.Id, damage.ArmorWear / armorStats.durability, Now);
            }

            var left = _health.Damage(victimId, damage.Health);
            Debug.Log("[raid] " + (shooterId ?? "something") + " hit " + victimId + " in the " + part.ToString().ToLowerInvariant()
                + " with " + ammoDefId + ": " + damage.Health.ToString("0.#", CultureInfo.InvariantCulture) + " damage"
                + (armorHit != null ? (damage.Penetrated ? ", through the armor" : ", stopped by the armor") : "")
                + ", " + left.ToString("0.#", CultureInfo.InvariantCulture) + " health left.");

            if (IsAI(victimId))
            {
                if (left <= 0f)
                {
                    AIDied(victimId);
                    return HitOutcome.Killed;
                }

                return HitOutcome.Hit;
            }

            var victim = ConnectionOfPlayer(victimId);
            SendHealth(victim);
            if (left <= 0f)
            {
                PlayerDied(victim);
                return HitOutcome.Killed;
            }

            InterruptExtraction(victim);
            return HitOutcome.Hit;
        }

        /// <summary>A round from one player hit another. See <see cref="Hit(string, string, string, BodyPart)"/>.</summary>
        public HitOutcome Hit(NetworkConnection victim, NetworkConnection shooter, string ammoDefId, BodyPart part) =>
            Hit(PlayerIdOf(victim), PlayerIdOf(shooter), ammoDefId, part);

        /// <summary>A player's or AI's health, or 0 if they aren't active in the raid.</summary>
        public float HealthOf(string actorId) => IsActive(actorId) && _health != null ? _health.Of(actorId) : 0f;

        /// <summary>The player's health, or 0 if they aren't in the raid.</summary>
        public float HealthOf(NetworkConnection connection) => HealthOf(PlayerIdOf(connection));

        private static T StatsOf<T>(string defId) where T : class, IItemStats
        {
            var asset = defId != null && ItemLibrary.Instance != null ? ItemLibrary.Instance.Find(defId) : null;
            return asset != null ? asset.Stats as T : null;
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

        /// <summary>Uses up rounds or a medkit a player or AI carries. False, changing nothing, if they don't have that many.</summary>
        public bool Consume(string actorId, string itemId, int quantity) =>
            IsActive(actorId) && _host.Consume(actorId, itemId, quantity, Now);

        /// <summary>Uses up rounds or a medkit the player carries. False, changing nothing, if they don't have that many.</summary>
        public bool Consume(NetworkConnection connection, string itemId, int quantity) => Consume(PlayerIdOf(connection), itemId, quantity);

        /// <summary>
        /// How many rounds the gun in a player's or AI's slot (SlotNames.Primary) can fire before reloading: its
        /// magazine's plus the chambered one, from the server's own inventory. 0 with no gun or nothing loaded.
        /// </summary>
        public int LoadedRounds(string actorId, string weaponSlot) => IsActive(actorId) ? _host.LoadedRounds(actorId, weaponSlot) : 0;

        /// <summary>See <see cref="LoadedRounds(string, string)"/>, for the player on this connection.</summary>
        public int LoadedRounds(NetworkConnection connection, string weaponSlot) => LoadedRounds(PlayerIdOf(connection), weaponSlot);

        /// <summary>
        /// Fires one round from the gun in a player's or AI's slot: the magazine's, then the chambered one. Returns
        /// the round's DefId, for its damage, or null if there was nothing to fire. A player's view of their gear
        /// follows within a second, as with <see cref="Consume"/>; show the count the player controller predicts.
        /// </summary>
        public string FireRound(string actorId, string weaponSlot) => IsActive(actorId) ? _host.FireRound(actorId, weaponSlot, Now) : null;

        /// <summary>See <see cref="FireRound(string, string)"/>, for the player on this connection.</summary>
        public string FireRound(NetworkConnection connection, string weaponSlot) => FireRound(PlayerIdOf(connection), weaponSlot);

        /// <summary>
        /// Reloads the gun in a player's or AI's slot: swaps its magazine for the fullest one that fits in their
        /// rig, then backpack, putting the old one where there's room. False, changing nothing, if there's no gun,
        /// no fuller magazine or no room. A player's client sees the change straight away.
        /// </summary>
        public bool Reload(string actorId, string weaponSlot)
        {
            if (!IsActive(actorId))
            {
                return false;
            }

            var output = _host.Reload(actorId, weaponSlot, out bool reloaded);
            Dispatch(output, ConnectionOfPlayer(actorId));
            return reloaded;
        }

        /// <summary>See <see cref="Reload(string, string)"/>, for the player on this connection.</summary>
        public bool Reload(NetworkConnection connection, string weaponSlot) => Reload(PlayerIdOf(connection), weaponSlot);

        // ---- AI ----

        /// <summary>
        /// Spawns an AI in the running raid: <paramref name="prefab"/> (a spawnable prefab with an
        /// <see cref="IRaidAI"/>) where it's told, carrying <paramref name="character"/> (AIKits.Build), which
        /// joins the raid inventory like a player's gear. Returns the AI's ID, which its IRaidAI components are
        /// given before it spawns, or null if there's no raid in progress. It fires, reloads and is hit by that ID
        /// through the calls above; at no health it dies, leaving a body with its gear, and is despawned. AI are
        /// the raid server's only: nothing about them goes to the backend.
        /// </summary>
        public string SpawnAI(NetworkObject prefab, Vector3 position, Quaternion rotation, ItemNode character)
        {
            if (!IsRunning || _lifecycle.Phase != RaidPhase.InProgress || prefab == null || character == null)
            {
                return null;
            }

            var aiId = "ai-" + Uuid7.NewId();
            _host.Service.AddPlayer(aiId, character);
            var spawned = Instantiate(prefab, position, rotation);
            foreach (var ai in spawned.GetComponents<IRaidAI>())
            {
                ai.SetUp(aiId);
            }

            var map = SceneManager.GetSceneByName(MapScene);
            _networkManager.ServerManager.Spawn(spawned, null, map.isLoaded ? map : SceneManager.GetActiveScene());
            _ai[aiId] = spawned;
            Debug.Log("[raid] Spawned " + aiId + " (" + prefab.name + ") at " + position + ".");
            return aiId;
        }

        // An AI ran out of health: its gear becomes a body where it stood, and it goes.
        private void AIDied(string aiId)
        {
            if (!_ai.TryGetValue(aiId, out var spawned))
            {
                return;
            }

            _ai.Remove(aiId);
            SpawnBody(Inventory.CharacterOf(aiId), spawned != null ? spawned.transform : null);
            _host.Kill(aiId);
            if (spawned != null && spawned.IsSpawned)
            {
                _networkManager.ServerManager.Despawn(spawned);
            }

            Debug.Log("[raid] " + aiId + " died.");
        }

        // Whether a player is in the raid, or an AI is alive in it.
        private bool IsActive(string actorId) =>
            IsInside(actorId) || (IsAI(actorId) && IsRunning && _lifecycle.Phase == RaidPhase.InProgress && _host.Service.IsActive(actorId));

        private NetworkConnection ConnectionOfPlayer(string playerId) =>
            playerId != null && _connectionOf.TryGetValue(playerId, out var connection) ? connection : null;

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

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[raid] A second RaidServer; using the newer one.");
            }

            Instance = this;
        }

        private void Start()
        {
            _networkManager = InstanceFinder.NetworkManager;

            // A dedicated server never runs the client's setup, so it turns a build's lag simulation off itself.
            NetworkConditions.Apply(_networkManager);
            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;

            SceneManager.sceneLoaded += OnSceneLoaded;

            if (_networkManager.IsServerStarted && MapLoaded())
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

            SceneManager.sceneLoaded -= OnSceneLoaded;
            StopRaid();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (_restartAt > 0f && Time.unscaledTime >= _restartAt)
            {
                _restartAt = 0f;
                if (!_networkManager.IsServerStarted)
                {
                    Debug.Log("[raid] Starting the server again for the next raid.");
                    _networkManager.ServerManager.StartConnection(); // StartRaid follows once it's listening
                }
            }

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
            if (args.ConnectionState == LocalConnectionState.Started && MapLoaded())
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

        // Currently hardcoded to SampleScene, eventually update with scene name connected to _mapID.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // The CI's scripted player runs from the server image, but it's a client: it mustn't listen too.
            if (ServerConfig.HasFlag("--ci-client"))
            {
                return;
            }

            if (scene.name != MapScene)
            {
                return;
            }

            if (_networkManager.IsServerStarted)
            {
                StartRaid(); // a dedicated server is already listening; the raid needed the map
            }
            else
            {
                _networkManager.ServerManager.StartConnection(); // StartRaid follows once it's listening
            }
        }

        private static bool MapLoaded() => SceneManager.GetSceneByName(MapScene).isLoaded;

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
                _testExtractAtSpawn = ServerConfig.HasFlag("--test-extract-at-spawn");

                // The map and network code go into the server's ready reports, so the backend only sends it
                // players on this map whose game runs the same networked code.
                var options = new BackendClientOptions { MapId = _mapId, NetworkCode = NetworkFingerprint.Current };
                _backend = new BackendClient(settings, catalog.Version, options, log: new UnityBackendLog());
                _backend.RaidAborted += OnRaidAborted;
                _backend.ReportRejected += rejection => Debug.LogError("[raid] The backend refused a report: " + rejection);
                _backend.Start();

                // One seed for the raid's loot, in the log so a raid's crates can be rolled again exactly.
                var lootSeed = ServerConfig.IntArgument("--loot-seed") ?? (_lootSeed != 0 ? _lootSeed : new System.Random().Next(1, int.MaxValue));
                Debug.Log("[raid] Loot seed " + lootSeed + " (--loot-seed " + lootSeed + ", or Loot Seed on RaidServer, rolls the same loot again)");
                var inventory = new RaidInventoryService(catalog);
                foreach (var container in FindObjectsByType<LootContainer>())
                {
                    inventory.AddContainer(container.BuildContents(catalog, lootSeed));
                }

                var looseLoot = PlaceLooseLoot(inventory, catalog, lootSeed);

                _lifecycle = new RaidLifecycle(new RaidLifecycleOptions
                {
                    Duration = TimeSpan.FromMinutes(minutes),
                    EmptyGrace = TimeSpan.FromSeconds(_emptyRaidSeconds),
                });
                _extraction = new ExtractionTracker();
                _health = new RaidHealth();
                _host = new RaidInventoryHost(inventory) { CanReach = CanReach };
                _host.ProtocolViolation += OnProtocolViolation;
                foreach (var (pileId, place) in looseLoot)
                {
                    SpawnGroundItem(pileId, place);
                }

                _networkManager.ServerManager.RegisterBroadcast<InventoryToServerBroadcast>(OnInventoryBroadcast);
                if (Application.isEditor || Debug.isDebugBuild)
                {
                    _networkManager.ServerManager.RegisterBroadcast<DevDieBroadcast>(OnDevDie);
                    _networkManager.ServerManager.RegisterBroadcast<DevShotBroadcast>(OnDevShot);
                    _networkManager.ServerManager.RegisterBroadcast<DevSpawnDummyBroadcast>(OnDevSpawnDummy);
                }

                // CI bakes the commit into the server image (RAIDZERO_BUILD); anything else is a local build.
                var build = System.Environment.GetEnvironmentVariable("RAIDZERO_BUILD");
                Debug.Log("[raid] Server ready: build " + (string.IsNullOrEmpty(build) ? "local" : build) + ", network code " + NetworkFingerprint.Current
                    + ", " + settings + ", map " + _mapId + ", catalog " + catalog.Version + ", " + minutes + "-minute raids, "
                    + ExtractionZone.All.Count + " extraction zone(s), " + looseLoot.Count + " loose item(s).");
                if (_testExtractAtSpawn)
                {
                    // For CI's raid test: players extract without moving, whatever the level has.
                    Debug.LogWarning("[raid] --test-extract-at-spawn: every player gets an extraction zone where they spawn. For tests only.");
                }
                else if (ExtractionZone.All.Count == 0)
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
                _networkManager.ServerManager.UnregisterBroadcast<DevDieBroadcast>(OnDevDie);
                _networkManager.ServerManager.UnregisterBroadcast<DevShotBroadcast>(OnDevShot);
                _networkManager.ServerManager.UnregisterBroadcast<DevSpawnDummyBroadcast>(OnDevSpawnDummy);
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
            _groundItems.Clear(); // the server stopping despawns them
            _lifecycle = null;
            _extraction = null;
            _health = null;
            _ai.Clear(); // the map's unloading takes their objects
            _closing = null;
            foreach (var zone in _testZones.Values)
            {
                if (zone != null)
                {
                    Destroy(zone.gameObject);
                }
            }

            _testZones.Clear();
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

                if (_testExtractAtSpawn && !_testZones.ContainsKey(playerId))
                {
                    // Tall, so a player spawned above the ground is still inside after they land.
                    _testZones[playerId] = ExtractionZone.Create("test_spawn", connection.FirstObject.transform.position, new Vector3(6, 40, 6), 8f);
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
                // In the editor or a windowed game there's no supervisor to start a fresh process, so stop
                // (dropping this raid's players, ground items and state) and start a new raid here a moment later,
                // once the transport has let go of the port.
                Debug.Log("[raid] Raid closed. The server stops, and starts again for a new raid in a moment.");
                _networkManager.ServerManager.StopConnection(true);
                _restartAt = Time.unscaledTime + 1f;
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

        // The player's own health, to them only.
        private void SendHealth(NetworkConnection connection)
        {
            var playerId = PlayerIdOf(connection);
            if (_health == null || playerId == null || connection == null || !connection.IsActive || !connection.IsAuthenticated)
            {
                return;
            }

            _networkManager.ServerManager.Broadcast(connection, new HealthBroadcast { Health = _health.Of(playerId), Max = _health.Max });
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

        // Only registered in the editor and Development builds.
        private void OnDevDie(NetworkConnection connection, DevDieBroadcast message, Channel channel)
        {
            Debug.Log("[raid] " + PlayerIdOf(connection) + " asked to die (dev).");
            PlayerDied(connection);
        }

        /// <summary>What the F1 overlay's Shot buttons hit you with.</summary>
        public const string DevShotAmmo = "ammo_545_ps";

        // Only registered in the editor and Development builds.
        private void OnDevShot(NetworkConnection connection, DevShotBroadcast message, Channel channel) =>
            Hit(connection, null, DevShotAmmo, message.Part);

        /// <summary>The prefab the F1 overlay's Spawn target dummy button spawns, by name.</summary>
        public const string TargetDummyPrefab = "TargetDummy";

        // Only registered in the editor and Development builds: a target dummy a few metres in front of the
        // player, facing them, carrying AIKits.TargetDummy.
        private void OnDevSpawnDummy(NetworkConnection connection, DevSpawnDummyBroadcast message, Channel channel)
        {
            var player = connection != null && connection.FirstObject != null ? connection.FirstObject.transform : null;
            if (player == null || !IsInside(PlayerIdOf(connection)))
            {
                return;
            }

            var prefab = FindAIPrefab(TargetDummyPrefab);
            if (prefab == null)
            {
                return;
            }

            var forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            if (forward == Vector3.zero)
            {
                forward = Vector3.forward;
            }

            var place = OnGround(player.position + forward * 4f + Vector3.up);
            SpawnAI(prefab, place, Quaternion.LookRotation(-forward, Vector3.up), AIKits.Build(Inventory.Catalog, AIKits.TargetDummy, Uuid7.NewId));
        }

        // A spawnable prefab with an IRaidAI on it, by name.
        private NetworkObject FindAIPrefab(string prefabName)
        {
            var prefabs = _networkManager.SpawnablePrefabs;
            for (int i = 0; prefabs != null && i < prefabs.GetObjectCount(); i++)
            {
                var candidate = prefabs.GetObject(true, i);
                if (candidate != null && candidate.name == prefabName && candidate.GetComponent<IRaidAI>() != null)
                {
                    return candidate;
                }
            }

            Debug.LogError("[raid] No spawnable prefab called " + prefabName + " with an IRaidAI on it. Make it with Raid Zero > AI > Create Target Dummy Prefab, and check it's in the NetworkManager's spawnable prefabs.");
            return null;
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
                SpawnGroundItem(pileId, DropPlace(sender));
                PileCreated?.Invoke(pileId, sender);
            }

            foreach (var pileId in output.RemovedRoots)
            {
                DespawnGroundItem(pileId);
                PileRemoved?.Invoke(pileId);
            }
        }

        // ---- Items on the ground ----

        /// <summary>Rolls every LooseLootSpot in the level and puts what they get on the ground: (pile, where it lies).</summary>
        private List<(string PileId, Vector3 Place)> PlaceLooseLoot(RaidInventoryService inventory, ItemCatalog catalog, int lootSeed)
        {
            var placed = new List<(string, Vector3)>();
            foreach (var spot in FindObjectsByType<LooseLootSpot>())
            {
                var items = spot.Roll(catalog, lootSeed);
                for (int i = 0; i < items.Count; i++)
                {
                    try
                    {
                        placed.Add((inventory.PlaceOnGround(items[i]), OnGround(spot.PlaceFor(i, items.Count) + Vector3.up * 0.1f)));
                    }
                    catch (ArgumentException exception)
                    {
                        Debug.LogWarning("[loot] " + spot.name + " rolled an item that can't lie on the ground: " + exception.Message, spot);
                    }
                }
            }

            return placed;
        }

        private void SpawnGroundItem(string pileId, Vector3 place)
        {
            var item = _host.Service.Snapshot(pileId)?.Children.FirstOrDefault();
            var prefab = GroundItemPrefab();
            if (item == null || prefab == null)
            {
                return;
            }

            var spin = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            var spawned = Instantiate(prefab, place, spin);
            var groundItem = spawned.GetComponent<GroundItem>();
            groundItem.Setup(pileId, item.Id, item.DefId);
            var map = SceneManager.GetSceneByName(MapScene);
            _networkManager.ServerManager.Spawn(spawned, null, map.isLoaded ? map : SceneManager.GetActiveScene());
            _groundItems[pileId] = groundItem;
        }

        private void DespawnGroundItem(string pileId)
        {
            if (_groundItems.TryGetValue(pileId, out var groundItem))
            {
                _groundItems.Remove(pileId);
                if (groundItem != null)
                {
                    _networkManager.ServerManager.Despawn(groundItem.NetworkObject);
                }
            }
        }

        /// <summary>The DroppedItem prefab, found in the NetworkManager's spawnable prefabs by its GroundItem.</summary>
        private NetworkObject GroundItemPrefab() => _groundItemPrefab != null ? _groundItemPrefab : _groundItemPrefab = FindPrefab<GroundItem>("DroppedItem");

        /// <summary>The Body prefab, found the same way.</summary>
        private NetworkObject BodyPrefab() => _bodyPrefab != null ? _bodyPrefab : _bodyPrefab = FindPrefab<Body>("Body");

        private NetworkObject FindPrefab<T>(string expected) where T : Component
        {
            var prefabs = _networkManager.SpawnablePrefabs;
            for (int i = 0; prefabs != null && i < prefabs.GetObjectCount(); i++)
            {
                var candidate = prefabs.GetObject(true, i);
                if (candidate != null && candidate.GetComponent<T>() != null)
                {
                    return candidate;
                }
            }

            Debug.LogError("[raid] No spawnable prefab has a " + typeof(T).Name + ". The " + expected + " prefab should, and it must be in the NetworkManager's spawnable prefabs.");
            return null;
        }

        /// <summary>A body where the player (or AI) stands, on the ground, facing the way they faced.</summary>
        private void SpawnBody(string containerId, Transform player)
        {
            var prefab = BodyPrefab();
            if (prefab == null || containerId == null)
            {
                return;
            }

            var place = player != null ? OnGround(player.position + Vector3.up) : Vector3.zero;
            var facing = player != null ? Quaternion.Euler(0f, player.eulerAngles.y, 0f) : Quaternion.identity;
            var spawned = Instantiate(prefab, place, facing);
            spawned.GetComponent<Body>().Setup(containerId);
            var map = SceneManager.GetSceneByName(MapScene);
            _networkManager.ServerManager.Spawn(spawned, null, map.isLoaded ? map : SceneManager.GetActiveScene());
        }

        /// <summary>
        /// Just in front of the player, on the ground: short of any wall in the way, and a little to one side
        /// so several drops in a row don't land inside each other.
        /// </summary>
        private static Vector3 DropPlace(NetworkConnection dropper)
        {
            var body = dropper != null && dropper.FirstObject != null ? dropper.FirstObject.transform : null;
            if (body == null)
            {
                return Vector3.zero;
            }

            var chest = body.position + Vector3.up;
            var forward = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
            float distance = DropDistance;
            if (Physics.Raycast(chest, forward, out var wall, DropDistance + 0.2f, ~0, QueryTriggerInteraction.Ignore))
            {
                distance = Mathf.Max(0f, wall.distance - 0.25f);
            }

            var jitter = UnityEngine.Random.insideUnitCircle * 0.15f;
            return OnGround(chest + forward * distance + new Vector3(jitter.x, 0f, jitter.y));
        }

        /// <summary>The first surface below a point, or the point itself if there's none within a few metres.</summary>
        private static Vector3 OnGround(Vector3 point)
        {
            return Physics.Raycast(point, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : point;
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
