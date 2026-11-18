using FishNet.Transporting;
using FishNet.Component.Prediction;
using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Prediction;
using UnityEngine;
using UnityEngine.InputSystem;
using FishNet.Object.Synchronizing;
using RaidZero.Net;
using RaidZero.Items;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

namespace RaidZero.Shared
{

    public class PlayerController : LivingEntity, IDamageable
    {

        // Inputs to send to the server.
        public struct PlayerReplicatedData : IReplicateData
        {
            public Vector2 moveInput;
            public Vector2 lookInput; // degrees to turn this tick (x right, y up), the player's settings already applied
            public bool jumpInput;
            public bool sprintInput;
            public bool fireInput;
            public bool fireModeInput;
            public bool reloadInput;
            private uint _tick;

            public PlayerReplicatedData(Vector2 moveInput, Vector2 lookInput, bool jumpInput, bool sprintInput, bool fireInput, bool fireModeInput, bool reloadInput) : this()
            {
                this.moveInput = moveInput;
                this.lookInput = lookInput;
                this.jumpInput = jumpInput;
                this.sprintInput = sprintInput;
                this.fireInput = fireInput;
                this.fireModeInput = fireModeInput;
                this.reloadInput = reloadInput;

            }

            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        // Values to reconcile from the server.
        public struct PlayerReconcileData : IReconcileData
        {
            public MoveController.MoveState moveState;
            public bool triggerReleasedSinceFire;
            public int selectedFireMode;
            public int loadedRounds;
            public float timeSinceLastShot;
            public bool reloading;
            public float reloadTimer;

            public PlayerReconcileData(MoveController.MoveState moveState, bool triggerReleasedSinceFire, int selectedFireMode, int loadedRounds, float timeSinceLastShot, bool reloading, float reloadTimer) : this()
            {
                this.moveState = moveState;
                this.triggerReleasedSinceFire = triggerReleasedSinceFire;
                this.selectedFireMode = selectedFireMode;
                this.loadedRounds = loadedRounds;
                this.timeSinceLastShot = timeSinceLastShot;
                this.reloading = reloading;
                this.reloadTimer = reloadTimer;

            }

            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        public float semiAutoRoundsPerMinute = 480f; // Maximum rounds per minute for semi-auto fire mode. Used in single shot fire mode if this is lower than the weapon's rounds per minute stat. Default value 480 is approximately the world record for semiauto shooting, so probably okay if players aren't allowed to exceed it.
        public float raycastRange = 300f;
        // Mouse sensitivity and invert look are the player's settings (GameSettings), applied on the client
        // before the look input is sent, so the server turns the player just as far.

        [SerializeField] private Transform _cameraMount;
        private Camera _heldCamera;
        [SerializeField] private CharacterController _characterController;

        [SerializeField] private GameObject _muzzle;

        private Vector2 currentMoveInput;
        private Vector2 currentLookInput;
        private Vector3 currentVelocity = Vector3.zero;
        private bool isJumping = false;
        private bool isRunning = false;
        private bool isFiring = false;

        private bool isFireMode = false;
        private bool isReloading = false;
        private bool triggerReleasedSinceFire = true;
        private float horizontalFallVelocity = 0f;

        // Locking this to primary but at some point will implement weapon switching.
        private string _heldSlot = SlotNames.Primary;
        // What's loaded: the magazine's rounds plus the chambered one. The server counts it from its own inventory
        // every tick; clients only predict from it (counting down as they fire), and the reconcile corrects them.
        private ItemNode _heldWeaponItem;
        private int ammoCount;
        private int selectedFireMode = 0;
        private float timeSinceLastShot = Mathf.Infinity;
        // Currently reloads instantaneous so this timer not used yet.
        private float reloadTimer = 0f;
        private bool reloading = false;

        private WeaponStats heldWeaponStats;
        private AmmoStats chamberedAmmoStats; // the round the next shot fires: the magazine's, then the chamber's
        private MagazineStats loadedMagazineStats;
        private bool _listeningToInventory;
        private PlayerReplicatedData lastReplicatedData = default;
        private uint predictionLimit = 3;

        /// <summary>This client's own player, for the HUD; null when there isn't one.</summary>
        public static PlayerController Local { get; private set; }

        /// <summary>Whether there's a gun in hand.</summary>
        public bool HasGun => heldWeaponStats != null;

        /// <summary>Rounds loaded in the gun in hand (magazine plus chamber): the count this client predicts.</summary>
        public int LoadedRounds => ammoCount;

        /// <summary>The fire mode selected on the gun in hand ("Single", "Auto"), or null with no gun.</summary>
        public string FireModeName
        {
            get
            {
                int count = heldWeaponStats?.fireModes?.Count ?? 0;
                return count > 0 ? heldWeaponStats.fireModes[selectedFireMode >= 0 && selectedFireMode < count ? selectedFireMode : 0].ToString() : null;
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!IsOwner) return;
            Local = this;
            // Enable the PlayerInput component for the local player
            GetComponent<PlayerInput>().enabled = true;

            if (Camera.main != null)
            {
                _heldCamera = Camera.main;
                Camera.main.transform.SetParent(_cameraMount);
                Camera.main.transform.SetPositionAndRotation(_cameraMount.position, _cameraMount.rotation);
            }
            Cursor.lockState = CursorLockMode.Locked;

            // The gun in hand, for its stats; the server reads its own inventory instead (RefreshFromServer).
            var inventory = RaidInventoryClient.Instance;
            if (inventory != null)
            {
                inventory.View.ContainerChanged += OnContainerChanged;
                _listeningToInventory = true;
                if (!IsServerStarted && inventory.View.Character != null)
                {
                    ReadHeldWeapon(inventory.View.Character.Root);
                }
            }
        }

        // The camera is the scene's, borrowed while this player is ours. Give it back before the player is
        // destroyed (extracting, dying, disconnecting) so the client still has a camera even if their player has died.
        public override void OnStopClient()
        {
            base.OnStopClient();
            if (Local == this)
            {
                Local = null;
            }

            if (_listeningToInventory && RaidInventoryClient.Instance != null)
            {
                RaidInventoryClient.Instance.View.ContainerChanged -= OnContainerChanged;
            }

            _listeningToInventory = false;
            if (_heldCamera != null && _heldCamera.transform.IsChildOf(transform))
            {
                _heldCamera.transform.SetParent(null, true);
            }

            _heldCamera = null;
        }

        public override void OnStartNetwork()
        {
            base.TimeManager.OnTick += OnTick;
            base.TimeManager.OnPostTick += OnPostTick;
        }

        public override void OnStopNetwork()
        {
            base.TimeManager.OnTick -= OnTick;
            base.TimeManager.OnPostTick -= OnPostTick;

        }
        public void OnMove(InputValue value)
        {
            currentMoveInput = value.Get<Vector2>();
        }

        public void OnLook(InputValue value)
        {
            currentLookInput += value.Get<Vector2>();
        }

        public void OnJump(InputValue value)
        {
            if (value.isPressed)
            {
                isJumping = true;
            }
        }

        public void OnSprint(InputValue value)
        {
            isRunning = value.isPressed;
        }

        public void OnFire(InputValue value)
        {
            isFiring = value.isPressed;
        }

        public void OnFireMode(InputValue value)
        {
            if (value.isPressed)
            {
                isFireMode = true;
            }
        }

        public void OnReload(InputValue value)
        {
            if (value.isPressed)
            {
                isReloading = true;
            }
        }

        public void OnTick()
        {
            DoReplicate(IsOwner ? GetReplicatedData() : default);
        }

        private void OnContainerChanged(ContainerView changed)
        {
            // Not on a host, whose server side reads the live inventory every tick anyway.
            if (changed.Kind != RootKind.Character || IsServerStarted) return;
            // A fresh copy of the gun's stats when our gear changes. Not the ammo count: the copy lags up to a
            // second behind shots, so the count comes from the reconcile.
            ReadHeldWeapon(changed.Root);
        }

        // Server only, each tick: the gun in hand and what's loaded, from the server's own inventory, so reloads,
        // swapped magazines and spent rounds count straight away. The reconcile then carries the count to the client.
        private void RefreshFromServer()
        {
            var raid = RaidServer.Instance;
            if (raid == null || !raid.IsRunning)
            {
                ReadHeldWeapon(null);
                ammoCount = 0;
                return;
            }

            ReadHeldWeapon(raid.Inventory.Find(raid.CharacterIdOf(Owner)));
            ammoCount = raid.LoadedRounds(Owner, _heldSlot);
        }

        // The stats of the gun in hand, its magazine and the round it fires next. Everything is cleared first, so
        // nothing lingers from a gun that's no longer in hand. Null character: no gun.
        private void ReadHeldWeapon(ItemNode character)
        {
            _heldWeaponItem = character?.ChildInSlot(_heldSlot);
            heldWeaponStats = StatsOf<WeaponStats>(_heldWeaponItem);
            var magazine = _heldWeaponItem?.ChildInSlot(SlotNames.Magazine);
            loadedMagazineStats = StatsOf<MagazineStats>(magazine);
            chamberedAmmoStats = StatsOf<AmmoStats>(magazine?.ChildInSlot(SlotNames.Rounds) ?? _heldWeaponItem?.ChildInSlot(SlotNames.Chamber));
        }

        // An item's stats from its asset, or null: no item, no asset, no stats, or stats of another kind.
        private static T StatsOf<T>(ItemNode item) where T : class, IItemStats
        {
            if (item == null || ItemLibrary.Instance == null)
            {
                return null;
            }

            var asset = ItemLibrary.Instance.Find(item.DefId);
            return asset != null ? asset.Stats as T : null;
        }

        private PlayerReplicatedData GetReplicatedData()
        {
            PlayerReplicatedData data = new PlayerReplicatedData(currentMoveInput, GameSettings.LookDegrees(currentLookInput), isJumping, isRunning, isFiring, isFireMode, isReloading);

            // Any accumulated inputs should be reset after sending to the server to avoid sending the same input multiple times.
            isJumping = false;
            isFireMode = false;
            isReloading = false;

            // If we eventually support gamepads, this will need to check if we're on a pad.
            currentLookInput = Vector2.zero;
            return data;
        }

        [Replicate]
        private void DoReplicate(PlayerReplicatedData data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            float deltaTime = (float)base.TimeManager.TickDelta;
            _random.Sync(data.GetTick());
            timeSinceLastShot += deltaTime;

            if (IsServerStarted)
            {
                RefreshFromServer();
            }

            bool skipUpdate = false;

            // Collect data and run predictions if you are a client but not the owner.
            if (!IsServerStarted && !IsOwner)
            {
                // If this is a regular tick, save the replicated data for future predictions.
                if (state.ContainsTicked())
                {
                    lastReplicatedData = data;
                }
                else if (state.IsFuture())
                {
                    if (data.GetTick() - lastReplicatedData.GetTick() > predictionLimit)
                    {
                        // Too far in the future, don't predict anything.
                        skipUpdate = true;
                    }
                    else
                    {
                        // Not yet too far in the future, predict based on the last replicated data.
                        data = lastReplicatedData;

                        // Any inputs that a player is unlikely to repeat should be unset.
                        data.jumpInput = false;
                        data.fireModeInput = false;
                        data.reloadInput = false;
                        data.lookInput = Vector2.zero;
                    }
                }
            }

            if (skipUpdate)
            {
                data.moveInput = Vector2.zero;
                data.lookInput = Vector2.zero;
                data.jumpInput = false;
                data.sprintInput = false;
                data.fireInput = false;
                data.reloadInput = false;
                data.fireModeInput = false;
            }

            _moveController.Replicate(new MoveController.MoveActions(data.moveInput, data.lookInput, data.jumpInput, data.sprintInput));

            // Gun Handling
            // Reloading is instant on the server for now (both guns' reloadTime is 0): it swaps in the fullest
            // magazine from the rig or backpack, and the reconcile brings the client the new count.
            // TODO: wait reloadTime first, and predict it on the client.
            // Moved actual reload to top
            // if (data.reloadInput && IsServerStarted && RaidServer.Instance != null && RaidServer.Instance.Reload(Owner, _heldSlot))
            // {
            //     RefreshFromServer();
            //     if (!state.ContainsReplayed()) Debug.Log("Reloaded: " + ammoCount + " rounds.");
            // }
            if (data.reloadInput)
            {
                if (!reloading && reloadTimer <= 0f)
                {
                    reloading = true;
                    // Once reloading takes time, set this to the reload time derived from magazine stats
                    reloadTimer = 0f;
                }
            }

            if (reloading)
            {
                // Reloads are instantaneous so currently timer will always be 0.
                if (reloadTimer > 0)
                {
                    reloadTimer -= deltaTime;
                }
                else
                {
                    // if timer is <= 0 the reload is complete, attempt the reload and set to false.
                    bool reloadResult = false;
                    if (IsServerStarted)
                    {

                        reloadResult = RaidServer.Instance.Reload(Owner, _heldSlot);
                        RefreshFromServer();
                        if (reloadResult && !state.ContainsReplayed()) Debug.Log("Reloaded: " + ammoCount + " rounds.");
                    }
                    else
                    {
                        // Eventually predict reload success or failure on client.
                        reloadResult = true;
                    }

                    reloading = false;
                }
            }

            int fireModeCount = heldWeaponStats?.fireModes?.Count ?? 0;
            if (data.fireModeInput && fireModeCount > 0)
            {
                selectedFireMode = (selectedFireMode + 1) % fireModeCount;
                if (!state.ContainsReplayed()) Debug.Log("Switched fire mode to " + heldWeaponStats.fireModes[selectedFireMode]);
            }

            if (data.fireInput)
            {
                if (!reloading) Fire(state, deltaTime);
            }
            else
            {
                triggerReleasedSinceFire = true;
            }

        }
        private void Fire(ReplicateState state, float deltaTime)
        {
            // No gun in hand, or one whose stats aren't filled in: nothing to fire.
            int fireModeCount = heldWeaponStats?.fireModes?.Count ?? 0;
            if (fireModeCount == 0 || heldWeaponStats.roundsPerMinute <= 0f) return;
            // A gun with fewer fire modes than the last one.
            if (selectedFireMode < 0 || selectedFireMode >= fireModeCount) selectedFireMode = 0;

            FireMode fireMode = heldWeaponStats.fireModes[selectedFireMode];
            float shotInterval = 60f / heldWeaponStats.roundsPerMinute;
            // Handle Single shot mode
            if (fireMode == FireMode.Single)
            {
                if (!triggerReleasedSinceFire) return;
                // No faster than a finger can go, and no faster than the gun itself fires.
                shotInterval = Mathf.Max(shotInterval, 60f / semiAutoRoundsPerMinute);
            }

            if (timeSinceLastShot < shotInterval)
            {
                if (!state.ContainsReplayed()) Debug.Log("Not ready to fire");
                return;
            }

            // Keep the time left over, so the rate isn't rounded to whole ticks (650 RPM at 60 ticks a second is a
            // shot every 5.5 ticks, not 6). More than a tick left over means the trigger wasn't held: start afresh,
            // so time spent not firing isn't banked as a burst.
            float leftOver = timeSinceLastShot - shotInterval;
            timeSinceLastShot = leftOver < deltaTime ? leftOver : 0f;

            if (ammoCount <= 0) return;

            // Only the server fires a real round, and needs to know which (for its damage). A client just predicts
            // the count: its copy of the gear lags behind shots, so it doesn't look there.
            string ammoDefId = null;
            if (IsServerStarted)
            {
                ammoDefId = RaidServer.Instance.FireRound(Owner, _heldSlot);
                // Nothing there after all: set ammo to 0 and it'll be counted again next tick.
                if (ammoDefId == null)
                {
                    ammoCount = 0;
                    return;
                }
            }

            ammoCount--;
            triggerReleasedSinceFire = false;

            // Deviation from weapon accuracy. Accuracy stat is in MOA (1/60 of a degree).
            float accDegrees = heldWeaponStats.accuracy / 60f;
            Vector2 deviationVectorDegrees = _random.NextInCircle(accDegrees);
            Quaternion deviation = Quaternion.Euler(-deviationVectorDegrees.y, deviationVectorDegrees.x, 0);
            Vector3 shootDirection = _muzzle.transform.rotation * deviation * Vector3.forward;
            // Trigger boxes (the ones around loot on the ground and bodies, for picking up and searching) don't stop bullets.
            Vector3 muzzle = _muzzle.transform.position;
            bool hitResult = Physics.Raycast(muzzle, shootDirection, out RaycastHit hit, raycastRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Vector3 endPoint = hitResult ? hit.point : muzzle + shootDirection * raycastRange;
            if (hitResult && IsServerStarted)
            {
                hit.collider.gameObject.TryGetComponent(out IDamageable damageable);
                damageable?.Hit(Owner, ammoDefId);
            }

            // Placeholder visuals for shooting. The shooter draws their own straight away (once, not on replays);
            // everyone else sees it when the server says so. A dedicated server draws nothing.
            if (IsOwner && !state.ContainsReplayed())
            {
                ShowTracer(muzzle, endPoint);
            }

            if (IsServerStarted)
            {
                ShowShotToOthers(muzzle, endPoint);
            }

            // Do recoil. Simple kick on camera view and rotation for now.
            float verticalRecoil = heldWeaponStats.recoilVertical * 0.2f;
            float horizontalRecoil = _random.NextFloatRange(-heldWeaponStats.recoilHorizontal, heldWeaponStats.recoilHorizontal) * 0.2f;

            _moveController.Recoil(new Vector2(horizontalRecoil, verticalRecoil));

        }

        // Other players' shots: their clients can't fire them (they don't know the shooter's gun), so the server
        // tells everyone but the shooter, who drew it already. Cosmetic, so unreliable; on a host, its own client
        // gets it too.
        [ObserversRpc(ExcludeOwner = true)]
        private void ShowShotToOthers(Vector3 start, Vector3 end, Channel channel = Channel.Unreliable)
        {
            ShowTracer(start, end);
        }

        private void ShowTracer(Vector3 start, Vector3 end)
        {
            if (TryGetComponent(out HitscanProjectileRender tracer))
            {
                tracer.ShowTracer(start, end);
            }
        }

        public void OnPostTick()
        {

            CreateReconcile();
        }

        public override void CreateReconcile()
        {
            PlayerReconcileData data = new(_moveController.GetMoveState(), triggerReleasedSinceFire, selectedFireMode, ammoCount, timeSinceLastShot, reloading, reloadTimer);
            DoReconcile(data);
        }

        [Reconcile]
        private void DoReconcile(PlayerReconcileData data, Channel channel = Channel.Unreliable)
        {
            _moveController.Reconcile(data.moveState);
            triggerReleasedSinceFire = data.triggerReleasedSinceFire;
            selectedFireMode = data.selectedFireMode;
            ammoCount = data.loadedRounds;
            timeSinceLastShot = data.timeSinceLastShot;
            reloading = data.reloading;
            reloadTimer = data.reloadTimer;
        }
        public void Update()
        {
            if (!IsOwner) return;

        }

        public void Hit(NetworkConnection source, string ammoDefID)
        {
            if (IsServerStarted)
            {
                // TODO: Create separate hitboxes and check here.
                RaidServer.Instance?.Hit(Owner, source, ammoDefID, BodyPart.Torso);
            }
        }
    }
}