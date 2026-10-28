using FishNet.Transporting;
using FishNet.Component.Prediction;
using FishNet.Connection;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Object.Prediction;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : NetworkBehaviour
{
    // Inputs to send to the server.
    public struct PlayerReplicatedData : IReplicateData
    {
        public Vector2 moveInput;
        public Vector2 lookInput;
        public bool jumpInput;
        public bool sprintInput;
        private uint _tick;

        public PlayerReplicatedData(Vector2 moveInput, Vector2 lookInput, bool jumpInput, bool sprintInput) : this()
        {
            this.moveInput = moveInput;
            this.lookInput = lookInput;
            this.jumpInput = jumpInput;
            this.sprintInput = sprintInput;
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

        public PlayerReconcileData(Vector3 position, Quaternion rotation, Vector3 velocity, float xRotation) : this()
        {
            this.position = position;
            this.rotation = rotation;
            this.velocity = velocity;
            this.xRotation = xRotation;
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

    public float lookSensitivity = 1f;
    public bool invertLook = false;

    [SerializeField] private Transform _cameraMount;
    [SerializeField] private CharacterController _characterController;

    private Vector2 currentMoveInput;
    private Vector2 currentLookInput;
    private float xRotation = 0f;
    private Vector3 currentVelocity = Vector3.zero;
    private bool isJumping = false;
    private bool isRunning = false;
    private float currentMoveSpeed = 0f;
    private bool wasGroundedLastFrame = true;
    private float horizontalFallVelocity = 0f;
    private PlayerReplicatedData lastReplicatedData = default;
    public override void OnStartClient()
    {
        if (!IsOwner) return;
        // Enable the PlayerInput component for the local player
        GetComponent<PlayerInput>().enabled = true;

        if(Camera.main != null)
        {
            Camera.main.transform.SetParent(_cameraMount);
            Camera.main.transform.SetPositionAndRotation(_cameraMount.position, _cameraMount.rotation);
        }
        Cursor.lockState = CursorLockMode.Locked;
        currentMoveSpeed = walkSpeed;

        base.TimeManager.OnTick += OnTick;
        base.TimeManager.OnPostTick += OnPostTick;
    }

    public override void OnStopClient()
    {
        if (!IsOwner) return;
        base.TimeManager.OnTick -= OnTick;
        base.TimeManager.OnPostTick -= OnPostTick;
    }

    public void OnMove(InputValue value)
    {
        currentMoveInput = value.Get<Vector2>();
    }

    public void OnLook(InputValue value)
    {
        currentLookInput = value.Get<Vector2>();
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
        Debug.Log("Sprint input received: " + value.isPressed);
        isRunning = value.isPressed;
    }
    public void OnTick()
    {
        DoReplicate(GetReplicatedData());
    }

    private PlayerReplicatedData GetReplicatedData()
    {
        if (invertLook)
        {
            currentLookInput.y = -currentLookInput.y;
        }
        PlayerReplicatedData data = new PlayerReplicatedData(currentMoveInput, currentLookInput, isJumping, isRunning);
        // Any one time inputs (like jump) should be reset after sending to the server to avoid sending the same input multiple times.
        isJumping = false;
        return data;
    }

    [Replicate]
    private void DoReplicate(PlayerReplicatedData data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
    {
        float deltaTime = (float)base.TimeManager.TickDelta;

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
                if (data.GetTick() - lastReplicatedData.GetTick() > 1)
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
                }
            }
        }

        if (skipUpdate)
        {
            data.moveInput = Vector2.zero;
            data.lookInput = Vector2.zero;
            data.jumpInput = false;
            data.sprintInput = false;
        }

        transform.Rotate(Vector3.up, data.lookInput.x * lookSensitivity * deltaTime);
        xRotation -= data.lookInput.y * lookSensitivity * deltaTime;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        _cameraMount.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

        if (_characterController.isGrounded)
        {
            currentVelocity.y = -2f; // Small negative value to keep the player grounded
        }

        if(data.moveInput.magnitude < 0.1f)
        {
            // Apply ground or air friction when no input is given
            currentVelocity.x *= _characterController.isGrounded ? friction : airFriction;
            currentVelocity.z *= _characterController.isGrounded ? friction : airFriction;
        }
        else
        {
            if(_characterController.isGrounded)
            {
                // Reset velocity when there is input on the ground.
                currentVelocity.x = 0f;
                currentVelocity.z = 0f;
            }
        }

        if(data.jumpInput && _characterController.isGrounded)
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
        if(!_characterController.isGrounded)
        {
            moveDirection *= airControl; // Reduce control in the air
        }
        currentMoveSpeed = data.sprintInput ? runSpeed : walkSpeed;
        currentVelocity += currentMoveSpeed * moveDirection;

        // Check horizontal velocity and limit air speed to the character's horizontal velocity when they left the ground.
        Vector3 horizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
        if(!_characterController.isGrounded && horizontalVelocity.magnitude > horizontalFallVelocity)
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
    }
    public void OnPostTick()
    {
        
        CreateReconcile();
    }

    public override void CreateReconcile()
    {
        PlayerReconcileData data = new PlayerReconcileData(transform.position, transform.rotation, currentVelocity, xRotation);
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
