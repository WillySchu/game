using RaidZero.Net;
using RaidZero.Raid;
using RaidZero.Shared.Inventory;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace RaidZero.Shared
{
    /// <summary>
    /// The target dummy's stand-in for the AI body (<see cref="IAIBody"/>), on the server, until John's AIBody
    /// drives the character motor and the gun: it slides straight towards where the brain sends it (no NavMesh:
    /// a wall in the way stops it, and after a few seconds of no progress it reports stuck), keeps to the ground,
    /// and turns to look. Its health and rounds are its real ones on the raid server, and reloading really swaps
    /// its magazine (instantly: reload time comes with the gun). It can't shoot: when the brain pulls the trigger,
    /// the AI debug view says so, and nothing fires. Its moving and turning reach clients through the
    /// NetworkTransform on its prefab.
    /// </summary>
    public sealed class TargetDummyBody : MonoBehaviour, IRaidAI, IAIBody
    {
        private const float WalkSpeed = 2f;
        private const float RunSpeed = 4.5f;
        private const float ArriveWithin = 0.4f;
        private const float ProgressCheckSeconds = 2f;
        private const int StuckAfterChecks = 3;

        [Tooltip("How fast it turns to look, in degrees per second.")]
        [SerializeField] private float _turnSpeed = 240f;

        [Tooltip("Its eyes and muzzle, above its feet, for its line of fire.")]
        [SerializeField] private float _muzzleHeight = 1.45f;

        private string _aiId;
        private string _weaponSlot;
        private int _capacity;
        private bool _canReload = true;
        private Vector3? _goal;
        private bool _run;
        private Vector3? _lookAt;
        private Vector3 _progressFrom;
        private float _progressCheckAt;
        private int _noProgress;

        public void SetUp(string aiId) => _aiId = aiId;

        /// <summary>Whether the brain is holding the trigger, for the debug view.</summary>
        public bool TriggerHeld { get; private set; }

        public NVector3 Position => ToN(transform.position);

        public NVector3 Forward => ToN(Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized);

        public float HealthFraction
        {
            get
            {
                var raid = RaidServer.Instance;
                return raid != null && _aiId != null ? raid.HealthOf(_aiId) / DamageRules.MaxHealth : 0f;
            }
        }

        public int LoadedRounds
        {
            get
            {
                var raid = RaidServer.Instance;
                if (raid == null || _aiId == null)
                {
                    return 0;
                }

                // Its gun's slot: the rifle's, or a looter's pistol's. Found once, while it's loaded.
                if (_weaponSlot == null)
                {
                    _weaponSlot = raid.LoadedRounds(_aiId, SlotNames.Primary) > 0 ? SlotNames.Primary : SlotNames.Secondary;
                }

                var rounds = raid.LoadedRounds(_aiId, _weaponSlot);
                _capacity = Mathf.Max(_capacity, rounds);
                return rounds;
            }
        }

        public int Capacity => Mathf.Max(_capacity, 1);

        public bool CanReload => _canReload;

        public bool IsReloading => false;

        public bool HasArrived => !_goal.HasValue;

        public bool IsStuck => _goal.HasValue && _noProgress >= StuckAfterChecks;

        public void MoveTo(NVector3 point, bool run)
        {
            var goal = ToU(point);
            if (!_goal.HasValue || Vector3.Distance(_goal.Value, goal) > 0.25f)
            {
                _noProgress = 0;
                _progressFrom = transform.position;
                _progressCheckAt = Time.time + ProgressCheckSeconds;
            }

            _goal = goal;
            _run = run;
        }

        public void Stop() => _goal = null;

        public void LookAt(NVector3 point) => _lookAt = ToU(point);

        public void SetTrigger(bool held) => TriggerHeld = held;

        public void Reload()
        {
            var raid = RaidServer.Instance;
            if (raid != null && _aiId != null && _weaponSlot != null)
            {
                _canReload = raid.Reload(_aiId, _weaponSlot);
            }
        }

        public bool HasLineOfFire(NVector3 point)
        {
            var muzzle = transform.position + Vector3.up * _muzzleHeight;
            var target = ToU(point);
            for (int i = 0; i < 4; i++)
            {
                if (!Physics.Linecast(muzzle, target, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    return true;
                }

                if (!hit.transform.IsChildOf(transform))
                {
                    return hit.collider.GetComponentInParent<IDamageable>() != null; // a person (its target, or someone in the way)
                }

                muzzle = hit.point + (target - muzzle).normalized * 0.05f; // past its own collider
            }

            return false;
        }

        // Server only: nothing tells it where to go on clients.
        private void Update()
        {
            if (_aiId == null)
            {
                return;
            }

            if (_lookAt.HasValue)
            {
                var flat = Vector3.ProjectOnPlane(_lookAt.Value - transform.position, Vector3.up);
                if (flat.sqrMagnitude > 0.01f)
                {
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flat, Vector3.up), _turnSpeed * Time.deltaTime);
                }
            }

            if (!_goal.HasValue)
            {
                return;
            }

            var to = Vector3.ProjectOnPlane(_goal.Value - transform.position, Vector3.up);
            if (to.magnitude <= ArriveWithin)
            {
                _goal = null;
                return;
            }

            var step = Mathf.Min(to.magnitude, (_run ? RunSpeed : WalkSpeed) * Time.deltaTime);
            var direction = to.normalized;
            var knee = transform.position + Vector3.up * 0.5f;
            if (!Physics.Raycast(knee, direction, out var wall, step + 0.4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || wall.transform.IsChildOf(transform))
            {
                var next = transform.position + direction * step;
                if (Physics.Raycast(next + Vector3.up * 1f, Vector3.down, out var ground, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    next.y = ground.point.y;
                }

                transform.position = next;
            }

            if (Time.time >= _progressCheckAt)
            {
                _noProgress = Vector3.Distance(_progressFrom, transform.position) < 1f ? _noProgress + 1 : 0;
                _progressFrom = transform.position;
                _progressCheckAt = Time.time + ProgressCheckSeconds;
            }
        }

        private static NVector3 ToN(Vector3 v) => new NVector3(v.x, v.y, v.z);

        private static Vector3 ToU(NVector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
