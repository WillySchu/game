using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : NetworkBehaviour
{
    
    public float walkSpeed = 5f;
    public float runSpeed = 10f;
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
    private Vector3 horizontalFallVelocity = Vector3.zero;
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
        if (value.isPressed && _characterController.isGrounded)
        {
            isJumping = true;
        }
    }

    public void OnSprint(InputValue value)
    {
        Debug.Log("Sprint input received: " + value.isPressed);
        isRunning = value.isPressed;
    }

    public void Update()
    {
        if (!IsOwner) return;

        
        transform.Rotate(Vector3.up, currentLookInput.x * lookSensitivity * Time.deltaTime);
        if(invertLook)
        {
            currentLookInput.y = -currentLookInput.y;
        }
        xRotation -= currentLookInput.y * lookSensitivity * Time.deltaTime;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        _cameraMount.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        
        if (_characterController.isGrounded)
        {
            currentVelocity.y = -2f; // Small negative value to keep the player grounded

        }

        if(currentMoveInput.magnitude < 0.1f)
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

        if(isJumping)
        {
            currentVelocity.y = Mathf.Sqrt(2f * jumpHeight * Mathf.Abs(gravity));
            Debug.Log(currentVelocity.y);
            isJumping = false;
        }
        else
        {
            currentVelocity.y += gravity * Time.deltaTime;
        }

        Vector3 moveDirection = transform.right * currentMoveInput.x + transform.forward * currentMoveInput.y;

        if(moveDirection.magnitude > 1f)
        {
            moveDirection.Normalize();
        }
        if(!_characterController.isGrounded)
        {
            moveDirection *= airControl; // Reduce control in the air
        }
        currentMoveSpeed = isRunning ? runSpeed : walkSpeed;
        
        currentVelocity += currentMoveSpeed * moveDirection;
        // Check horizontal velocity and limit air speed to the character's horizontal velocity when they left the ground.
        Vector3 horizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
        if(!_characterController.isGrounded && horizontalVelocity.magnitude > horizontalFallVelocity.magnitude)
        {
            // Limit horizontal velocity in the air to the maximum speed when leaving the ground.
            horizontalVelocity = horizontalVelocity.normalized * horizontalFallVelocity.magnitude;
            currentVelocity.x = horizontalVelocity.x;
            currentVelocity.z = horizontalVelocity.z;
        }

        _characterController.Move(currentVelocity * Time.deltaTime);

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
    }

    private void OnLeftGround()
    {
        // Store the horizontal velocity so we know the max speed the player should be allowed to reach while in the air.
        horizontalFallVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);

    }
    
    private void OnTouchedGround()
    {
        // Handle logic for when the player touches the ground. Nothing for now.
    }
    
    
}
