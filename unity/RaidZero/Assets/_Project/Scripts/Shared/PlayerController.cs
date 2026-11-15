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
        // Ammo count derived from chambered round + magazine count.
        private ItemNode _heldWeaponItem;
        private int ammoCount;
        private int selectedFireMode = 0;
        private float timeSinceLastShot = Mathf.Infinity;

        private WeaponStats heldWeaponStats;
        private AmmoStats chamberedAmmoStats;
        private MagazineStats loadedMagazineStats;
        private PlayerReplicatedData lastReplicatedData = default;
        private uint predictionLimit = 3;
        

        // Used in replicates to produce the same random values on clients and server. Base seed set on construction, and state is updated during each replicate.
        private readonly SyncVar<uint> _baseSeed = new();
        private Utils.SyncedRandom _random;


        public override void OnStartServer()
        {
            _baseSeed.Value = (uint)Random.Range(int.MinValue, int.MaxValue);
        }
        public override void OnStartClient()
        {
            if (!IsOwner) return;
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

            _random = new Utils.SyncedRandom(_baseSeed.Value);

            RaidInventoryClient.Instance.View.ContainerChanged += OnContainerChanged;
        }

        // The camera is the scene's, borrowed while this player is ours. Give it back before the player is
        // destroyed (extracting, dying, disconnecting) so the client still has a camera even if their player has died.
        public override void OnStopClient()
        {
            base.OnStopClient();
            RaidInventoryClient.Instance.View.ContainerChanged -= OnContainerChanged;
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

            UpdateHeldItemStats();
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
            if(changed.Kind != RootKind.Character) return;
            // Have the client grab a fresh copy of the held item stats when we recieve a change.
            // Not strictly needed until we have visuals to change on inventory swap.
            UpdateHeldItemStats();
        }
        // Pull stats from the weapon held in hands.
        private void UpdateHeldItemStats()
        {
            ammoCount = 0;
            ItemNode character = null;
            if (IsOwner)
            {
                if(RaidInventoryClient.Instance.View.Character != null)
                {
                    character = RaidInventoryClient.Instance.View.Character.Root;
                }
            }
            else if (IsServerStarted)
            {
                // Grab directly from the RaidServer inventory. Added an Instance variable to RaidServer for this, we can remove it if we don't want to do that.
                string characterInventoryId = RaidServer.Instance.CharacterIdOf(Owner);
                character = RaidServer.Instance.Inventory.Find(characterInventoryId);
            }
            if (character != null)
            {
                ItemNode heldItem = character.ChildInSlot(_heldSlot);
                if (heldItem != null)
                {
                    _heldWeaponItem = heldItem;
                    ItemDefinitionAsset itemDef = ItemLibrary.Instance.Find(heldItem.DefId);
                    if(itemDef.Stats.itemType == ItemTags.Weapon)
                    {
                        heldWeaponStats = (WeaponStats) itemDef.Stats;
                    }

                    // Replace with SlotNames reference once that's updated on backend.
                    ItemNode chamber = heldItem.ChildInSlot("chamber");
                    if(chamber != null)
                    {
                        ItemDefinitionAsset chamberDef = ItemLibrary.Instance.Find(chamber.DefId);
                        if(chamberDef.Stats.itemType == ItemTags.Ammo)
                        {
                            chamberedAmmoStats = (AmmoStats) chamberDef.Stats;
                            ammoCount++;
                        }
                    }
                    ItemNode magazine = heldItem.ChildInSlot(SlotNames.Magazine);
                    if(magazine != null)
                    {
                        ItemDefinitionAsset magazineDef = ItemLibrary.Instance.Find(magazine.DefId);
                        if(magazineDef.Stats.itemType == ItemTags.Magazine)
                        {
                            loadedMagazineStats = (MagazineStats) magazineDef.Stats;
                        }

                        ItemNode magazineAmmo = magazine.ChildInSlot(SlotNames.Rounds);
                        if(magazineAmmo != null)
                        {
                            ammoCount += magazineAmmo.Quantity;
                        }

                    }
                }
            }
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
            if(data.fireModeInput)
            {
                selectedFireMode = (selectedFireMode + 1) % heldWeaponStats.fireModes.Count;
                Debug.Log("Switched fire mode to " + heldWeaponStats.fireModes[selectedFireMode]);
            }

            if (data.fireInput)
            {
                Fire();
            }
            else
            {
                triggerReleasedSinceFire = true;
            }
        }

        private void Fire()
        {
            // Make sure our weapon stats are up to date:
            UpdateHeldItemStats();
            // If there isn't a weapon in hand, can't shoot.
            if (heldWeaponStats == null) return;
            FireMode fireMode = heldWeaponStats.fireModes[selectedFireMode];
            float shotInterval = 60f / heldWeaponStats.roundsPerMinute;
            // Handle Single shot mode
            if (fireMode == FireMode.Single)
            {
                if (!triggerReleasedSinceFire) return;
                shotInterval = 60f / semiAutoRoundsPerMinute;
            }
            if (timeSinceLastShot > shotInterval)
            {
                timeSinceLastShot = 0f;
                
                if (ammoCount <= 0) return;
                ammoCount--;

                // Server actually attempts to consume ammo.
                if (IsServerStarted)
                {
                    // Consume ammo from magazine or chamber, fail if none (though ammoCount should be up to date and should have stopped us from getting here).
                    // TODO: Replace this directly calling Consume with new functions.
                    ItemNode ammoStack = _heldWeaponItem.ChildInSlot(SlotNames.Magazine)?.ChildInSlot(SlotNames.Rounds) ?? _heldWeaponItem.ChildInSlot("chamber");
                    if(ammoStack == null) return;
                    RaidServer.Instance.Consume(Owner, ammoStack.Id, 1);
                }
                // Pew Pew
                Debug.Log("Pew Pew! " + ammoCount + " left.");
                triggerReleasedSinceFire = false;
                
            }
            else
            {
                Debug.Log("Not ready to fire");
            }

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