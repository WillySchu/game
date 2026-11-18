using RaidZero.Net;
using RaidZero.Raid;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// Runs an AI's brain (<see cref="AIBrain"/>) on the server, five times a second, staggered: it reads what the
    /// <see cref="AIPerceiver"/> beside it knows and drives the <see cref="IAIBody"/> beside it (the target dummy's
    /// stand-in, <see cref="TargetDummyBody"/>, until John's AIBody). AI from spawn zones think, with their
    /// profile's settings (or their faction's) and their zone's patrol route; a target dummy from the F1 overlay
    /// only does if it was spawned as a thinking one. Being shot (<see cref="WasHit"/>) sends it towards the
    /// shooter and tells its squad. It fills in its part of the AI debug view: its state, where it's going and
    /// its cover.
    /// </summary>
    public sealed class AIBrainRunner : MonoBehaviour, IRaidAI, IAIDebugSource, IAIDevThinking
    {
        [Tooltip("How often it decides, per second.")]
        [Range(1f, 20f)]
        [SerializeField] private float _ticksPerSecond = 5f;

        private string _aiId;
        private AIProfile _profile;
        private AISpawnZone _zone;
        private bool _thinks;
        private AIBrain _brain;
        private SquadMemory _squad;
        private float _nextTickAt;

        /// <summary>Its brain, on the server once it's thinking; null otherwise.</summary>
        public AIBrain Brain => _brain;

        public void SetUp(string aiId) => _aiId = aiId;

        /// <summary>The spawn zone that spawned it says what it is (before it's networked): it thinks.</summary>
        public void Assign(AIProfile profile, AISpawnZone zone)
        {
            _profile = profile;
            _zone = zone;
            _thinks = true;
        }

        public void ThinkForDev() => _thinks = true;

        /// <summary>A shot hit it, from that player or AI (null if nobody's: the F1 overlay's Shot buttons).</summary>
        public void WasHit(string shooterId)
        {
            if (_brain == null)
            {
                return;
            }

            var raid = RaidServer.Instance;
            System.Numerics.Vector3? from = null;
            if (shooterId != null && raid != null && raid.TryGetBody(shooterId, out var shooter))
            {
                var p = shooter.position;
                from = new System.Numerics.Vector3(p.x, p.y, p.z);
            }

            _brain.WasHit(Time.time, shooterId, from);
        }

        private void Update()
        {
            var raid = RaidServer.Instance;
            if (!_thinks || _aiId == null || raid == null || !raid.IsRunning)
            {
                return;
            }

            if (_brain == null && !TryWake())
            {
                return;
            }

            if (Time.time >= _nextTickAt)
            {
                _nextTickAt = Mathf.Max(_nextTickAt + 1f / _ticksPerSecond, Time.time);
                _brain.Tick(Time.time);
            }
        }

        // The brain, once the perceiver beside it has started.
        private bool TryWake()
        {
            var perceiver = GetComponent<AIPerceiver>();
            var body = GetComponent<IAIBody>();
            if (perceiver == null || perceiver.Perception == null || body == null)
            {
                return false;
            }

            var settings = _profile != null ? _profile.Brain : AIBrainSettings.For(perceiver.Faction);
            var route = _zone != null && _zone.Route != null ? _zone.Route : null;
            var points = new System.Collections.Generic.List<System.Numerics.Vector3>();
            if (route != null)
            {
                foreach (var point in route.Points)
                {
                    points.Add(new System.Numerics.Vector3(point.x, point.y, point.z));
                }
            }

            _brain = new AIBrain(_aiId, body, perceiver.Perception, settings, UnityAIWorld.Instance, points, route == null || route.Loop, _aiId.GetHashCode());
            perceiver.TurnsToLook = false;
            _squad = perceiver.Perception.Squad;
            _nextTickAt = Time.time + Random.value / _ticksPerSecond; // staggered
            return true;
        }

        private void OnDestroy()
        {
            // Despawned without dying (the raid ended): out of the squad's count all the same.
            if (_brain != null && _brain.State != AIState.Dead)
            {
                _squad?.Leave(_aiId);
            }
        }

        public void Describe(ref AIDebugEntry entry)
        {
            if (_brain == null)
            {
                return;
            }

            entry.Brain = _brain.Describe();
            if (_brain.MoveTarget.HasValue)
            {
                var m = _brain.MoveTarget.Value;
                entry.HasMoveTarget = true;
                entry.MoveTarget = new Vector3(m.X, m.Y, m.Z);
            }

            if (_brain.Cover.HasValue)
            {
                var c = _brain.Cover.Value.Position;
                entry.HasCover = true;
                entry.Cover = new Vector3(c.X, c.Y, c.Z);
            }

            if (_brain.Firing && GetComponent<TargetDummyBody>() != null)
            {
                entry.Brain += " (a dummy can't shoot)";
            }
        }
    }
}
