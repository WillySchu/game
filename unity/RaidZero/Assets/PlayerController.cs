using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : NetworkBehaviour
{
    
    public float walkSpeed = 5f;
    public float runSpeed = 10f;
    public float jumpHeight = 2.5f;
    public float gravity = -9.81f;

    public float friction = 0.1f; // Friction factor to slow down the player when no input is given

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
        

        if (currentMoveInput.magnitude < 0.1f && _characterController.isGrounded)
        {
            // Apply friction when no input is given and we're on the ground
            currentVelocity.x *= friction;
            currentVelocity.z *= friction;
        }
        else
        {
            // Clear velocity when there is new input
            currentVelocity.x = 0f;
            currentVelocity.z = 0f;
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
        currentMoveSpeed = isRunning ? runSpeed : walkSpeed;
        currentVelocity += currentMoveSpeed * moveDirection;
        _characterController.Move(currentVelocity * Time.deltaTime);
    }
    
}
