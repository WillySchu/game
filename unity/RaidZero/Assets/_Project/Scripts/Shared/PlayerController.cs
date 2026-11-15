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
    public class PlayerController : NetworkBehaviour
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
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 velocity;
            public float xRotation;
            public bool wasGroundedLastFrame;
            public float horizontalFallVelocity;
            public float currentMoveSpeed;
            public bool triggerReleasedSinceFire;
            public int selectedFireMode;
            public int loadedRounds;
            public float timeSinceLastShot;

            public PlayerReconcileData(Vector3 position, Quaternion rotation, Vector3 velocity, float xRotation, bool wasGroundedLastFrame, float horizontalFallVelocity, float currentMoveSpeed, bool triggerReleasedSinceFire, int selectedFireMode, int loadedRounds, float timeSinceLastShot) : this()
            {
                this.position = position;
                this.rotation = rotation;
                this.velocity = velocity;
                this.xRotation = xRotation;
                this.wasGroundedLastFrame = wasGroundedLastFrame;
                this.horizontalFallVelocity = horizontalFallVelocity;
                this.currentMoveSpeed = currentMoveSpeed;
                this.triggerReleasedSinceFire = triggerReleasedSinceFire;
                this.selectedFireMode = selectedFireMode;
                this.loadedRounds = loadedRounds;
                this.timeSinceLastShot = timeSinceLastShot;
            }
            
            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        } 


        public float walkSpeed = 5f;
        public float runSpeed = 10f;

        public float airSpeedMinLimit = 2.5f; // Player will always be allowed to accelerate to at least this speed in the air, even if they left the ground at a lower speed.
        public float jumpHeight = 1.5f;
        public float airControl = 0.05f; // Control factor when in the air (0 = no control, 1 = full control)
        public float gravity = -9.81f;

        public float friction = 0.1f; // Friction factor to slow down the player when no input is given. (0= full stop, 1= no friction)
        public float airFriction = 1f; // Friction factor to slow down the player when in the air and no input is given. (0= full stop, 1= no friction)
        public float semiAutoRoundsPerMinute = 480f; // Maximum rounds per minute for semi-auto fire mode. Used in single shot fire mode if this is lower than the weapon's rounds per minute stat. Default value 480 is approximately the world record for semiauto shooting, so probably okay if players aren't allowed to exceed it.

        // Mouse sensitivity and invert look are the player's settings (GameSettings), applied on the client
        // before the look input is sent, so the server turns the player just as far.

        [SerializeField] private Transform _cameraMount;
        private Camera _heldCamera;
        [SerializeField] private CharacterController _characterController;

        private Vector2 currentMoveInput;
        private Vector2 currentLookInput;
        private float xRotation = 0f;
        private Vector3 currentVelocity = Vector3.zero;
        private bool isJumping = false;
        private bool isRunning = false;
        private bool isFiring = false;
        
        private bool isFireMode = false;
        private bool isReloading = false;
    
        private float currentMoveSpeed = 0f;
        private bool wasGroundedLastFrame = true;
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

        private WeaponStats heldWeaponStats;
        private AmmoStats chamberedAmmoStats; // the round the next shot fires: the magazine's, then the chamber's
        private MagazineStats loadedMagazineStats;
        private bool _listeningToInventory;
        private PlayerReplicatedData lastReplicatedData = default;
        private uint predictionLimit = 3;
        

        // Used in replicates to produce the same random values on clients and server. Base seed set on construction, and state is updated during each replicate.
        private readonly SyncVar<uint> _baseSeed = new();
        private Utils.SyncedRandom _random;


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

        public override void OnStartServer()
        {
            _baseSeed.Value = (uint)Random.Range(int.MinValue, int.MaxValue);
            ResetRandom();
        }

        public override void OnStartClient()
        {
            // Every copy of the player needs the same random numbers as the server's, not just the owner's.
            ResetRandom();
            if (!IsOwner) return;
            Local = this;
            // Enable the PlayerInput component for the local player
            GetComponent<PlayerInput>().enabled = true;

            if(Camera.main != null)
            {
                _heldCamera = Camera.main;
                Camera.main.transform.SetParent(_cameraMount);
                Camera.main.transform.SetPositionAndRotation(_cameraMount.position, _cameraMount.rotation);
            }
            Cursor.lockState = CursorLockMode.Locked;
            currentMoveSpeed = walkSpeed;

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

        private void ResetRandom() => _random = new Utils.SyncedRandom(_baseSeed.Value);

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
            DoReplicate( IsOwner ? GetReplicatedData() : default);
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
            if(!IsServerStarted && !IsOwner)
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

            transform.Rotate(Vector3.up, data.lookInput.x);
            xRotation -= data.lookInput.y;
            xRotation = Mathf.Clamp(xRotation, -90f, 90f);

            _cameraMount.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

            // Whether the last Move ended on the ground, from our own state rather than the CharacterController.
            // Its isGrounded is only as fresh as its last Move: after a reconcile puts the player back where the
            // server had them, it still describes wherever the client had got to (mid-jump, say), so the first
            // tick replayed after it would refuse a jump the server accepted. wasGroundedLastFrame is the same
            // fact, but it's part of the reconcile state, so it's right after a reset too.
            bool grounded = wasGroundedLastFrame;

            if (grounded)
            {
                currentVelocity.y = -2f; // Small negative value to keep the player grounded
            }

            if(data.moveInput.magnitude < 0.1f)
            {
                // Apply ground or air friction when no input is given
                currentVelocity.x *= grounded ? friction : airFriction;
                currentVelocity.z *= grounded ? friction : airFriction;
            }
            else
            {
                if(grounded)
                {
                    // Reset velocity when there is input on the ground.
                    currentVelocity.x = 0f;
                    currentVelocity.z = 0f;
                }
            }

            if(data.jumpInput && grounded)
            {
                currentVelocity.y = Mathf.Sqrt(2f * jumpHeight * Mathf.Abs(gravity));
            }
            else
            {
                currentVelocity.y += gravity * deltaTime;
            }

            Vector3 moveDirection = transform.right * data.moveInput.x + transform.forward * data.moveInput.y;
            if(moveDirection.magnitude > 1f)
            {
                moveDirection.Normalize();
            }
            if(!grounded)
            {
                moveDirection *= airControl; // Reduce control in the air
            }
            currentMoveSpeed = data.sprintInput ? runSpeed : walkSpeed;
            currentVelocity += currentMoveSpeed * moveDirection;

            // Check horizontal velocity and limit air speed to the character's horizontal velocity when they left the ground.
            Vector3 horizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
            if(!grounded && horizontalVelocity.magnitude > horizontalFallVelocity)
            {
                // Limit horizontal velocity in the air to the maximum speed when leaving the ground.
                horizontalVelocity = horizontalVelocity.normalized * horizontalFallVelocity;
                currentVelocity.x = horizontalVelocity.x;
                currentVelocity.z = horizontalVelocity.z;
            }

            _characterController.Move(currentVelocity * deltaTime);
            
            if(wasGroundedLastFrame && !_characterController.isGrounded)
            {
                // Player just left the ground
                OnLeftGround();
            }
            else if(!wasGroundedLastFrame && _characterController.isGrounded)
            {
                // Player just landed
                OnTouchedGround();
            }
            wasGroundedLastFrame = _characterController.isGrounded;

            // Gun Handling
            // Reloading is instant on the server for now (both guns' reloadTime is 0): it swaps in the fullest
            // magazine from the rig or backpack, and the reconcile brings the client the new count.
            // TODO: wait reloadTime first, and predict it on the client.
            if (data.reloadInput && IsServerStarted && RaidServer.Instance != null && RaidServer.Instance.Reload(Owner, _heldSlot))
            {
                RefreshFromServer();
                if (!state.IsReplayed()) Debug.Log("Reloaded: " + ammoCount + " rounds.");
            }

            int fireModeCount = heldWeaponStats?.fireModes?.Count ?? 0;
            if (data.fireModeInput && fireModeCount > 0)
            {
                selectedFireMode = (selectedFireMode + 1) % fireModeCount;
                if (!state.IsReplayed()) Debug.Log("Switched fire mode to " + heldWeaponStats.fireModes[selectedFireMode]);
            }

            if (data.fireInput)
            {
                Fire(state, deltaTime);
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
                if (!state.IsReplayed()) Debug.Log("Not ready to fire");
                return;
            }

            // Keep the time left over, so the rate isn't rounded to whole ticks (650 RPM at 60 ticks a second is a
            // shot every 5.5 ticks, not 6). More than a tick left over means the trigger wasn't held: start afresh,
            // so time spent not firing isn't banked as a burst.
            float leftOver = timeSinceLastShot - shotInterval;
            timeSinceLastShot = leftOver < deltaTime ? leftOver : 0f;

            if (ammoCount <= 0) return;

            // The server fires the real round, from its own inventory: the magazine's, then the chambered one.
            if (IsServerStarted && (RaidServer.Instance == null || RaidServer.Instance.FireRound(Owner, _heldSlot) == null))
            {
                ammoCount = 0; // nothing there after all; it's counted again next tick
                return;
            }

            ammoCount--;
            triggerReleasedSinceFire = false;
            // Pew Pew. Only the first time a tick runs: a client replays ticks after each reconcile.
            if (!state.IsReplayed()) Debug.Log("Pew Pew! " + ammoCount + " left.");
        }

        public void OnPostTick()
        {
            
            CreateReconcile();
        }

        public override void CreateReconcile()
        {
            PlayerReconcileData data = new PlayerReconcileData(transform.position, transform.rotation, currentVelocity, xRotation, wasGroundedLastFrame, horizontalFallVelocity, currentMoveSpeed, triggerReleasedSinceFire, selectedFireMode, ammoCount, timeSinceLastShot);
            DoReconcile(data);
        }

        [Reconcile]
        private void DoReconcile(PlayerReconcileData data, Channel channel = Channel.Unreliable)
        {
            _characterController.enabled = false; // Disable the CharacterController to avoid collision issues when setting position and rotation
            transform.position = data.position;
            transform.rotation = data.rotation;
            currentVelocity = data.velocity;
            xRotation = data.xRotation;
            wasGroundedLastFrame = data.wasGroundedLastFrame;
            horizontalFallVelocity = data.horizontalFallVelocity;
            currentMoveSpeed = data.currentMoveSpeed;
            triggerReleasedSinceFire = data.triggerReleasedSinceFire;
            selectedFireMode = data.selectedFireMode;
            ammoCount = data.loadedRounds;
            timeSinceLastShot = data.timeSinceLastShot;
            _cameraMount.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
            _characterController.enabled = true;
        }
        public void Update()
        {
            if (!IsOwner) return;

        }

        private void OnLeftGround()
        {
            // Store the horizontal velocity so we know the max speed the player should be allowed to reach while in the air.
            horizontalFallVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z).magnitude;
            if (horizontalFallVelocity < airSpeedMinLimit)
            {
                horizontalFallVelocity = airSpeedMinLimit;
            }

        }
        
        private void OnTouchedGround()
        {
            // Handle logic for when the player touches the ground. Nothing for now.
        }
        
        
    }
}