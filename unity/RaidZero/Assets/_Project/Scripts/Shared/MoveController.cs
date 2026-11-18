using UnityEngine;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using System.Collections.Generic;


namespace RaidZero.Shared
{
     [RequireComponent(typeof(CharacterController))]
     // Responsible for an entity's movement stats and movement logic.
     [RequireComponent(typeof(LivingEntity))]
     public class MoveController : NetworkBehaviour
     {
          [Tooltip("Camera mount on the character prefab to rotate while looking. If not set, the character will ignore up and down look input.")]
          [SerializeField] private GameObject _cameraMount;
          [Header("Movement Stats")]
          [Tooltip("This character's walk speed in m/s.")]
          [SerializeField] private float _walkSpeed = 5f;
          [Tooltip("This character's run speed in m/s.")]
          [SerializeField] private float _runSpeed = 10f;
          [Tooltip("If a player jumps while moving slower than this speed, they will be allowed to accelerate to at least this speed in the air.")]
          [SerializeField] private float _airSpeedMinLimit = 2.5f;
          [Tooltip("The height in meters that this character can jump.")]
          [SerializeField] private float _jumpHeight = 1f;
          [Tooltip("The control factor when in the air (0 = no control, 1 = full control).")]
          [SerializeField] private float _airControl = 0.05f;
          [Tooltip("The gravity this character experiences in m/s^2.")]
          [SerializeField] private float _gravity = -9.81f;
          [Tooltip("Friction applied to the character on the ground when no input is given. Multiplier applied per second. 0 = full stop, 1 = no friction.")]
          [SerializeField] private float _friction = 0.1f;
          [Tooltip("Friction applied to the character in the air when no input is given. Multiplier applied per second. 0 = full stop, 1 = no friction.")]
          [SerializeField] private float _airFriction = 1f;

          // Values that need reconciliation for client-side prediction.
          public struct MoveState
          {
               public Vector3 Position;
               public Quaternion Rotation;
               public Vector3 Velocity;
               public float CurrentMoveSpeed;
               public bool Grounded;
               public float XRotation;
               public float HorizontalFallVelocity;

               public MoveState(Vector3 position, Quaternion rotation, Vector3 velocity, float currentMoveSpeed, bool grounded, float xRotation, float horizontalFallVelocity)
               {
                    Position = position;
                    Rotation = rotation;
                    Velocity = velocity;
                    CurrentMoveSpeed = currentMoveSpeed;
                    Grounded = grounded;
                    XRotation = xRotation;
                    HorizontalFallVelocity = horizontalFallVelocity;
               }
          }

          // Actions requested from a player or other controller.
          public struct MoveActions
          {
               public Vector2 Move;
               public Vector2 Look;
               public bool Jump;
               public bool Run;

               public MoveActions(Vector2 move, Vector2 look, bool jump, bool run)
               {
                    Move = move;
                    Look = look;
                    Jump = jump;
                    Run = run;
               }
          }

          private MoveActions _currentActions;
          private float _currentDeltaTime;

          private MoveState _state;
          private CharacterController _controller;
          private LivingEntity _controllerEntity;

          public void Replicate(MoveActions actions)
          {
               _currentActions = actions;
               _currentDeltaTime = (float)base.TimeManager.TickDelta;
               Look();
               Move();
          }

          public MoveState GetMoveState() => _state;
          public void Reconcile(MoveState state)
          {
               _controller.enabled = false;
               _state = state;
               transform.SetPositionAndRotation(state.Position, state.Rotation);
               _controller.enabled = true;
          }

          private void Awake()
          {
               List<string> errors = new();
               _controller = GetComponent<CharacterController>();
               if (_controller == null)
               {
                    errors.Add("CharacterController component is required for MoveController.");
               }
               _controllerEntity = GetComponent<LivingEntity>();
               if (_controllerEntity == null)
               {
                    errors.Add("LivingEntity component is required for MoveController.");
               }

               if (errors.Count > 0)
               {
                    foreach (string error in errors)
                    {
                         Debug.LogError(error);
                    }
                    Debug.LogError("Disabling MoveController.");
                    enabled = false;
               }

               _state.CurrentMoveSpeed = _walkSpeed;
          }

          public void Look()
          {
               if (_cameraMount != null)
               {
                    // Rotate the camera mount up and down based on the look input.
                    float xRotation = _state.XRotation - _currentActions.Look.y;
                    xRotation = Mathf.Clamp(xRotation, -90f, 90f);
                    _cameraMount.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
                    _state.XRotation = xRotation;
               }
               // Rotate the character left and right based on the look input.
               transform.Rotate(Vector3.up * _currentActions.Look.x);
          }

          public void Move()
          {
               if (_state.Grounded)
               {
                    _state.Velocity.y = -2f; // Small downward force to keep the character grounded.
               }
               if (_currentActions.Move.magnitude < 0.1f)
               {
                    // Apply friction when input is below threshold.
                    float friction = _state.Grounded ? _friction : _airFriction;
                    friction *= _currentDeltaTime;
                    _state.Velocity.x *= friction;
                    _state.Velocity.z *= friction;
               }
               else
               {
                    // If input exists halt horizontal movement so the character isn't fighting its prior velocity.
                    if (_state.Grounded)
                    {
                         _state.Velocity.x = 0f;
                         _state.Velocity.z = 0f;
                    }
               }

               if (_currentActions.Jump && _state.Grounded)
               {
                    _state.Velocity.y = Mathf.Sqrt(_jumpHeight * -2f * _gravity);
               }
               else
               {
                    _state.Velocity.y += _gravity * _currentDeltaTime;
               }

               Vector3 move = transform.right * _currentActions.Move.x + transform.forward * _currentActions.Move.y;
               // Cap move input to 1 to prevent diagonal movement from being faster than straight movement.
               if (move.magnitude > 1f)
               {
                    move.Normalize();
               }

               _state.CurrentMoveSpeed = _currentActions.Run ? _runSpeed : _walkSpeed;
               _state.Velocity += _state.CurrentMoveSpeed * move;
               Vector3 horizontalVelocity = new Vector3(_state.Velocity.x, 0f, _state.Velocity.z);
               if (!_state.Grounded && horizontalVelocity.magnitude > _state.HorizontalFallVelocity)
               {
                    // Limit horizontal velocity in the air to the maximum speed when leaving the ground.
                    horizontalVelocity = horizontalVelocity.normalized * _state.HorizontalFallVelocity;
                    _state.Velocity.x = horizontalVelocity.x;
                    _state.Velocity.z = horizontalVelocity.z;
               }
               _controller.Move(_state.Velocity * _currentDeltaTime);

               if (_state.Grounded && !_controller.isGrounded)
               {
                    OnLeftGround();
               }
               else if (!_state.Grounded && _controller.isGrounded)
               {
                    OnTouchedGround();
               }

               // Store the grounded state after the move to use next tick.
               _state.Grounded = _controller.isGrounded;


          }

          public void OnLeftGround()
          {
               _state.HorizontalFallVelocity = new Vector3(_state.Velocity.x, 0f, _state.Velocity.z).magnitude;
               if (_state.HorizontalFallVelocity < _airSpeedMinLimit)
               {
                    _state.HorizontalFallVelocity = _airSpeedMinLimit;
               }
          }

          public void OnTouchedGround()
          {
               _state.HorizontalFallVelocity = 0f;
          }

          public void Recoil(Vector2 recoil)
          {
               if (_cameraMount != null)
               {
                    // Apply recoil to the camera mount.
                    float xRotation = _state.XRotation - recoil.y;
                    xRotation = Mathf.Clamp(xRotation, -90f, 90f);
                    _cameraMount.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
                    _state.XRotation = xRotation;
               }
               // Apply recoil to the character's rotation.
               transform.Rotate(Vector3.up * recoil.x);
          }

     }
}