using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace RaidZero.Raid
{
    /// <summary>How an AI sees, from its profile.</summary>
    public sealed class SightSettings
    {
        /// <summary>Beyond this, in metres, it sees nothing.</summary>
        public float ViewDistance { get; set; } = 80f;

        /// <summary>The whole width of its view cone, in degrees.</summary>
        public float ViewAngle { get; set; } = 120f;

        /// <summary>Awareness per second, at best (close, in the middle of the view, a still target: half this).</summary>
        public float FillRate { get; set; } = 1f;

        /// <summary>Awareness lost per second while a target is out of sight.</summary>
        public float DrainRate { get; set; } = 0.2f;

        /// <summary>Within this, in metres, distance doesn't slow it down.</summary>
        public float CloseDistance { get; set; } = 5f;
    }

    /// <summary>One look at one possible target, from the AI's eyes: what the body's sight check found.</summary>
    public struct SightSample
    {
        public string TargetId;

        /// <summary>Where the target is now.</summary>
        public Vector3 Position;

        /// <summary>Whether a line from the AI's eyes reaches the target's head or chest unblocked.</summary>
        public bool InLineOfSight;

        public float Distance;

        /// <summary>Degrees between where the AI faces and the target: 0 dead ahead.</summary>
        public float AngleOffCentre;

        /// <summary>How fast the target is moving, in metres per second.</summary>
        public float Speed;

        /// <summary>
        /// How easy the target is to make out where it stands, 0 to 1: lit and on open ground is 1. Dark, rain,
        /// foliage, fog and crouching bring it down once those exist; 1 until then.
        /// </summary>
        public float Visibility;
    }

    /// <summary>
    /// What one AI knows: an awareness of each target, 0 to 1, that fills while it's in view and drains when it
    /// isn't; where it last saw each one; and the last thing it heard. Plain C#, ticked by the AI's body a few
    /// times a second with what its sight checks found (<see cref="See"/>) and told about noises
    /// (<see cref="Hear"/>). Past <see cref="NoticeAt"/> it turns to look; at <see cref="AlertAt"/> it's alert,
    /// which is when the brain will fight. AI spawned together share sightings through a <see cref="SquadMemory"/>.
    /// </summary>
    public sealed class AIPerception
    {
        public const float NoticeAt = 0.35f;
        public const float AlertAt = 1f;

        /// <summary>How long a sound, or a squadmate's sighting, is worth looking towards.</summary>
        public const double HeardForSeconds = 10;

        /// <summary>How long it remembers where it last saw a target, once it's out of sight and forgotten.</summary>
        public const double RememberSeconds = 30;

        private readonly Dictionary<string, Known> _known = new Dictionary<string, Known>(StringComparer.Ordinal);
        private readonly SightSettings _sight;
        private readonly SquadMemory _squad;

        public AIPerception(SightSettings sight = null, SquadMemory squad = null)
        {
            _sight = sight ?? new SightSettings();
            _squad = squad;
        }

        /// <summary>The target it's most aware of, if it's noticed anyone; null otherwise.</summary>
        public string Focus { get; private set; }

        /// <summary>Whether it's fully aware of its focus.</summary>
        public bool IsAlert => Focus != null && AwarenessOf(Focus) >= AlertAt;

        public SquadMemory Squad => _squad;

        /// <summary>The last noise it heard, roughly where, and when; null for none.</summary>
        public (Vector3 Position, double Time)? Heard { get; private set; }

        public float AwarenessOf(string targetId) =>
            targetId != null && _known.TryGetValue(targetId, out var known) ? known.Awareness : 0f;

        /// <summary>Where it last saw the target, and when.</summary>
        public bool TryGetLastSeen(string targetId, out Vector3 position, out double time)
        {
            if (targetId != null && _known.TryGetValue(targetId, out var known) && known.SeenAt.HasValue)
            {
                position = known.LastSeen;
                time = known.SeenAt.Value;
                return true;
            }

            position = default;
            time = 0;
            return false;
        }

        /// <summary>
        /// One tick of sight: each sample fills or drains its target's awareness, and every target it knows that
        /// isn't in <paramref name="samples"/> drains. An alert sighting goes to the squad.
        /// </summary>
        public void See(double now, float deltaSeconds, IEnumerable<SightSample> samples)
        {
            var looked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sample in samples ?? Array.Empty<SightSample>())
            {
                if (sample.TargetId == null)
                {
                    continue;
                }

                looked.Add(sample.TargetId);
                if (!_known.TryGetValue(sample.TargetId, out var known))
                {
                    known = new Known();
                    _known[sample.TargetId] = known;
                }

                var rate = FillRate(_sight, sample);
                if (rate > 0f)
                {
                    known.Awareness = Math.Min(1f, known.Awareness + rate * deltaSeconds);
                    known.LastSeen = sample.Position;
                    known.SeenAt = now;
                    if (known.Awareness >= AlertAt)
                    {
                        _squad?.Report(sample.TargetId, sample.Position, now);
                    }
                }
                else
                {
                    known.Awareness = Math.Max(0f, known.Awareness - _sight.DrainRate * deltaSeconds);
                }
            }

            var forgotten = new List<string>();
            foreach (var pair in _known)
            {
                if (!looked.Contains(pair.Key))
                {
                    pair.Value.Awareness = Math.Max(0f, pair.Value.Awareness - _sight.DrainRate * deltaSeconds);
                }

                if (pair.Value.Awareness <= 0f && (!pair.Value.SeenAt.HasValue || now - pair.Value.SeenAt.Value > RememberSeconds))
                {
                    forgotten.Add(pair.Key);
                }
            }

            foreach (var id in forgotten)
            {
                _known.Remove(id);
            }

            Focus = null;
            float best = 0f;
            foreach (var pair in _known)
            {
                if (pair.Value.Awareness >= NoticeAt && pair.Value.Awareness > best)
                {
                    best = pair.Value.Awareness;
                    Focus = pair.Key;
                }
            }
        }

        /// <summary>A noise, heard roughly here (see <see cref="Hearing"/>).</summary>
        public void Hear(double now, Vector3 roughPosition) => Heard = (roughPosition, now);

        /// <summary>
        /// Where it wants to look, and why, in this order: at the target it's noticed (where it last saw it); at
        /// a noise it heard in the last <see cref="HeardForSeconds"/>; at a squadmate's sighting from then. False
        /// when there's nothing to look at.
        /// </summary>
        public bool TryGetLookPoint(double now, out Vector3 point, out string why)
        {
            if (Focus != null && TryGetLastSeen(Focus, out point, out _))
            {
                why = IsAlert ? "alert" : "noticed";
                return true;
            }

            if (Heard.HasValue && now - Heard.Value.Time <= HeardForSeconds)
            {
                point = Heard.Value.Position;
                why = "heard";
                return true;
            }

            if (_squad != null && _squad.TryGetLatest(now, HeardForSeconds, out _, out point, out _))
            {
                why = "squad";
                return true;
            }

            point = default;
            why = null;
            return false;
        }

        /// <summary>A line for the debug view: "alert: 1a2b3c4d (1.00)", "heard a shot 3 s ago", "calm".</summary>
        public string Describe(double now)
        {
            if (Focus != null)
            {
                return (IsAlert ? "alert: " : "noticed: ") + Short(Focus) + " (" + AwarenessOf(Focus).ToString("0.00", CultureInfo.InvariantCulture) + ")";
            }

            if (Heard.HasValue && now - Heard.Value.Time <= HeardForSeconds)
            {
                return "heard a shot " + Math.Floor(now - Heard.Value.Time).ToString(CultureInfo.InvariantCulture) + " s ago";
            }

            if (_squad != null && _squad.TryGetLatest(now, HeardForSeconds, out var id, out _, out var time))
            {
                return "squad saw " + Short(id) + " " + Math.Floor(now - time).ToString(CultureInfo.InvariantCulture) + " s ago";
            }

            return "calm";
        }

        /// <summary>
        /// How fast one sample fills awareness, per second: 0 if the target is out of sight, out of range or
        /// outside the view cone. Otherwise the fill rate, slowed by distance (to 15% at the edge of its view
        /// distance), by being off centre (to 35% at the cone's edge), by keeping still (half for a still target,
        /// up to one and a half for a sprinting one) and by the target's visibility.
        /// </summary>
        public static float FillRate(SightSettings sight, SightSample sample)
        {
            var halfAngle = sight.ViewAngle * 0.5f;
            if (!sample.InLineOfSight || sample.Distance > sight.ViewDistance || sample.AngleOffCentre > halfAngle || sample.Visibility <= 0f)
            {
                return 0f;
            }

            var far = sight.ViewDistance - sight.CloseDistance;
            var distanceFactor = sample.Distance <= sight.CloseDistance || far <= 0f
                ? 1f
                : Lerp(1f, 0.15f, (sample.Distance - sight.CloseDistance) / far);
            var coneFactor = halfAngle <= 0f ? 1f : Lerp(1f, 0.35f, sample.AngleOffCentre / halfAngle);
            var motionFactor = Math.Min(1.5f, Math.Max(0.5f, 0.5f + sample.Speed / 7f));
            return sight.FillRate * distanceFactor * coneFactor * motionFactor * Math.Min(1f, sample.Visibility);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Min(1f, Math.Max(0f, t));

        private static string Short(string id) => id == null ? "?" : id.StartsWith("ai-", StringComparison.Ordinal) ? id.Substring(0, Math.Min(id.Length, 11)) : id.Substring(0, Math.Min(id.Length, 8));

        private sealed class Known
        {
            public float Awareness;
            public Vector3 LastSeen;
            public double? SeenAt;
        }
    }

    /// <summary>What AI spawned together know between them: the latest sighting of each target, and when.</summary>
    public sealed class SquadMemory
    {
        private readonly Dictionary<string, (Vector3 Position, double Time)> _sightings = new Dictionary<string, (Vector3, double)>(StringComparer.Ordinal);

        public void Report(string targetId, Vector3 position, double time)
        {
            if (targetId != null && (!_sightings.TryGetValue(targetId, out var existing) || existing.Time <= time))
            {
                _sightings[targetId] = (position, time);
            }
        }

        /// <summary>The most recent sighting, if it's no older than <paramref name="maxAgeSeconds"/>.</summary>
        public bool TryGetLatest(double now, double maxAgeSeconds, out string targetId, out Vector3 position, out double time)
        {
            targetId = null;
            position = default;
            time = double.MinValue;
            foreach (var pair in _sightings)
            {
                if (pair.Value.Time > time && now - pair.Value.Time <= maxAgeSeconds)
                {
                    targetId = pair.Key;
                    position = pair.Value.Position;
                    time = pair.Value.Time;
                }
            }

            return targetId != null;
        }
    }

    /// <summary>
    /// How easy a target is to make out (<see cref="SightSample.Visibility"/>), from what's around it. Every
    /// factor is 1, neutral, until the thing it stands for exists: the body passes 1 for each for now.
    /// </summary>
    public static class Visibility
    {
        /// <param name="light">How lit the target is: 1 in daylight, towards 0 in the dark.</param>
        /// <param name="clarity">How clear the air between is: 1 clear, lower in fog, rain or through foliage.</param>
        /// <param name="posture">How much of the target shows: 1 standing, lower crouching or prone.</param>
        public static float Of(float light = 1f, float clarity = 1f, float posture = 1f) =>
            Math.Min(1f, Math.Max(0f, light)) * Math.Min(1f, Math.Max(0f, clarity)) * Math.Min(1f, Math.Max(0f, posture));
    }

    /// <summary>Who hears a noise, and where they think it came from.</summary>
    public static class Hearing
    {
        /// <summary>How far a gunshot carries, in metres.</summary>
        public const float GunshotRadius = 150f;

        /// <summary>How far off a listener's guess can be, as a share of how far away the noise was.</summary>
        public const float Vagueness = 0.15f;

        public static bool CanHear(Vector3 noise, float radius, Vector3 listener) => Vector3.Distance(noise, listener) <= radius;

        /// <summary>Where a listener thinks the noise was: off to one side by up to <see cref="Vagueness"/> of the distance.</summary>
        public static Vector3 Rough(Vector3 noise, Vector3 listener, Random random)
        {
            var off = Vector3.Distance(noise, listener) * Vagueness * (float)(random?.NextDouble() ?? 0.5);
            var angle = (float)((random?.NextDouble() ?? 0) * Math.PI * 2);
            return noise + new Vector3((float)Math.Cos(angle) * off, 0f, (float)Math.Sin(angle) * off);
        }
    }
}
