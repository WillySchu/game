using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RaidZero.Net;
using RaidZero.Raid;
using RaidZero.Shared.Raid;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace RaidZero.Shared
{
    /// <summary>
    /// An AI's eyes and ears, on the server: a few times a second (<see cref="_checksPerSecond"/>, staggered so
    /// AI don't all look on the same frame) it casts lines from its eyes to the head and chest of every player and
    /// hostile AI in range, and feeds what it saw to its <see cref="AIPerception"/>; it hears every shot within
    /// earshot (RaidServer.NoiseMade). Without a brain thinking (a target dummy from the F1 overlay) it does the
    /// one thing perception alone can: it turns to look at whoever it's noticed, or towards a shot it heard, or
    /// where its squad saw someone. With one (<see cref="AIBrainRunner"/>), the brain does the looking.
    ///
    /// Its sight comes from its profile (<see cref="AIProfile.Sight"/>); AI from the same spawn zone share a
    /// squad memory. Light, fog, rain and crouching are wired in but neutral (<see cref="Visibility"/>) until
    /// they exist. It also says what it's thinking for the F1 overlay's AI debug view (<see cref="IAIDebugSource"/>).
    /// Its turning reaches clients through the NetworkTransform on its prefab.
    /// </summary>
    public sealed class AIPerceiver : MonoBehaviour, IRaidAI, IAIDebugSource
    {
        /// <summary>Where a body's head and chest are, above its feet, for the lines it casts. Players' and dummies' alike.</summary>
        public const float HeadHeight = 1.55f;
        public const float ChestHeight = 1.15f;

        [Tooltip("Its eyes, above its feet.")]
        [SerializeField] private float _eyeHeight = 1.6f;

        [Tooltip("How often it looks, per second. Each look casts two lines per possible target in range.")]
        [Range(1f, 20f)]
        [SerializeField] private float _checksPerSecond = 8f;

        [Tooltip("How fast it turns to look, in degrees per second.")]
        [SerializeField] private float _turnSpeed = 240f;

        private readonly List<SightSample> _samples = new List<SightSample>();
        private readonly Dictionary<string, (Vector3 Position, float Time)> _lastPositions = new Dictionary<string, (Vector3, float)>();
        private string _aiId;
        private AIPerception _perception;
        private Faction _faction = Faction.Looters;
        private SightSettings _sight;
        private SquadMemory _squad;
        private System.Random _random;
        private RaidServer _listeningTo;
        private float _nextCheckAt;
        private float _lastCheckAt;

        /// <summary>What it knows, on the server; null on clients.</summary>
        public AIPerception Perception => _perception;

        public Faction Faction => _faction;

        /// <summary>Whether it turns the AI to look at what it notices: until a brain (AIBrainRunner) takes over looking.</summary>
        public bool TurnsToLook { get; set; } = true;

        public void SetUp(string aiId) => _aiId = aiId;

        /// <summary>The spawn zone that spawned it says what it is (before it's networked): its sight, its faction, its squad.</summary>
        public void Assign(AIProfile profile, AISpawnZone zone)
        {
            if (profile != null)
            {
                _faction = profile.Faction;
                _sight = profile.Sight;
            }

            _squad = zone != null ? zone.Squad : null;
        }

        private void Update()
        {
            var raid = RaidServer.Instance;
            if (_aiId == null || raid == null || !raid.IsRunning)
            {
                return;
            }

            if (_perception == null)
            {
                _perception = new AIPerception(_sight, _squad);
                _random = new System.Random(_aiId.GetHashCode());
                _lastCheckAt = Time.time;
                _nextCheckAt = Time.time + (float)_random.NextDouble() / _checksPerSecond; // staggered
            }

            if (_listeningTo != raid)
            {
                StopListening();
                _listeningTo = raid;
                raid.NoiseMade += OnNoise;
            }

            if (Time.time >= _nextCheckAt)
            {
                _nextCheckAt += 1f / _checksPerSecond;
                if (_nextCheckAt < Time.time)
                {
                    _nextCheckAt = Time.time + 1f / _checksPerSecond; // after a hitch, don't catch up
                }

                Look(raid);
            }

            if (TurnsToLook && _perception.TryGetLookPoint(Time.time, out var point, out _))
            {
                TurnTowards(new Vector3(point.X, point.Y, point.Z));
            }
        }

        private void OnDestroy() => StopListening();

        private void StopListening()
        {
            if (_listeningTo != null)
            {
                _listeningTo.NoiseMade -= OnNoise;
                _listeningTo = null;
            }
        }

        private void OnNoise(RaidNoise noise)
        {
            if (_perception == null || noise.SourceId == _aiId)
            {
                return;
            }

            var me = ToN(transform.position);
            var at = ToN(noise.Position);
            if (Hearing.CanHear(at, noise.Radius, me))
            {
                _perception.Hear(Time.time, Hearing.Rough(at, me, _random));
            }
        }

        // One look: a sample for every player, and every hostile AI, within view distance.
        private void Look(RaidServer raid)
        {
            var now = Time.time;
            var delta = Mathf.Max(0.01f, now - _lastCheckAt);
            _lastCheckAt = now;
            var settings = _sight ?? DefaultSight;
            _samples.Clear();

            foreach (var (id, body) in raid.PlayersInRaid())
            {
                Sample(id, body, settings, now);
            }

            foreach (var pair in raid.AI)
            {
                if (pair.Key == _aiId || pair.Value == null)
                {
                    continue;
                }

                var other = pair.Value.GetComponent<AIPerceiver>();
                var theirs = other != null ? other.Faction : Faction.Looters;
                if (Factions.AreHostile(_faction, theirs))
                {
                    Sample(pair.Key, pair.Value.transform, settings, now);
                }
            }

            _perception.See(now, delta, _samples);
        }

        private static readonly SightSettings DefaultSight = new SightSettings();

        private void Sample(string id, Transform body, SightSettings settings, float now)
        {
            var eye = transform.position + Vector3.up * _eyeHeight;
            var feet = body.position;
            var chest = feet + Vector3.up * ChestHeight;
            var distance = Vector3.Distance(eye, chest);

            // How fast it's moving, from where it was last look.
            float speed = 0f;
            if (_lastPositions.TryGetValue(id, out var last) && now > last.Time)
            {
                speed = Vector3.Distance(last.Position, feet) / (now - last.Time);
            }

            _lastPositions[id] = (feet, now);
            if (distance > settings.ViewDistance)
            {
                return; // too far to see: it drains like anyone out of sight
            }

            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            var towards = Vector3.ProjectOnPlane(chest - eye, Vector3.up);
            var angle = towards.sqrMagnitude < 0.0001f ? 0f : Vector3.Angle(forward, towards);
            var clear = angle <= settings.ViewAngle * 0.5f
                && (CanSee(eye, feet + Vector3.up * HeadHeight, body) || CanSee(eye, chest, body));

            _samples.Add(new SightSample
            {
                TargetId = id,
                Position = ToN(feet),
                InLineOfSight = clear,
                Distance = distance,
                AngleOffCentre = angle,
                Speed = speed,
                Visibility = Visibility.Of(LightAt(feet), ClarityBetween(eye, chest), PostureOf(body)),
            });
        }

        // Neutral until there's lighting, weather and crouching to read.
        private static float LightAt(Vector3 point) => 1f;
        private static float ClarityBetween(Vector3 from, Vector3 to) => 1f;
        private static float PostureOf(Transform body) => 1f;

        // Whether a line from its eyes reaches the target: nothing in the way but the target itself (and never
        // its own colliders, which the line may start inside or pass through).
        private bool CanSee(Vector3 eye, Vector3 point, Transform target)
        {
            var from = eye;
            for (int i = 0; i < 4; i++)
            {
                if (!Physics.Linecast(from, point, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    return true;
                }

                if (hit.transform == target || hit.transform.IsChildOf(target))
                {
                    return true;
                }

                if (!(hit.transform == transform || hit.transform.IsChildOf(transform)))
                {
                    return false;
                }

                from = hit.point + (point - from).normalized * 0.01f; // past its own collider
            }

            return false;
        }

        private void TurnTowards(Vector3 point)
        {
            var flat = Vector3.ProjectOnPlane(point - transform.position, Vector3.up);
            if (flat.sqrMagnitude < 0.01f)
            {
                return;
            }

            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flat, Vector3.up), _turnSpeed * Time.deltaTime);
        }

        public void Describe(ref AIDebugEntry entry)
        {
            entry.Id = _aiId;
            entry.LabelAt = transform.position + Vector3.up * 2.1f;
            entry.State = "calm";
            if (_perception == null)
            {
                entry.Detail = _faction.ToString();
                return;
            }

            var now = Time.time;
            if (_perception.TryGetLookPoint(now, out var look, out var why))
            {
                entry.State = why;
                entry.HasLookPoint = true;
                entry.LookPoint = new Vector3(look.X, look.Y, look.Z);
            }

            entry.Awareness = _perception.AwarenessOf(_perception.Focus);
            var detail = new StringBuilder(_faction.ToString()).Append(": ").Append(_perception.Describe(now));
            foreach (var sample in _samples)
            {
                var awareness = _perception.AwarenessOf(sample.TargetId);
                if (awareness > 0f && sample.TargetId != _perception.Focus)
                {
                    detail.Append("\n  ").Append(Short(sample.TargetId)).Append(' ')
                        .Append(awareness.ToString("0.00", CultureInfo.InvariantCulture))
                        .Append(sample.InLineOfSight ? " (in view)" : "");
                }
            }

            entry.Detail = detail.ToString();
        }

        private static string Short(string id) => id.Length > 11 ? id.Substring(0, 11) : id;

        private static NVector3 ToN(Vector3 v) => new NVector3(v.x, v.y, v.z);
    }
}
