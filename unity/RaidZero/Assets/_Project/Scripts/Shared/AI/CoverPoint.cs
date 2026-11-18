using System.Collections.Generic;
using RaidZero.Raid;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace RaidZero.Shared
{
    /// <summary>
    /// A place for AI to take cover, put by hand in a map for the greybox: stand it on the ground behind something
    /// solid, its blue arrow (its forward) pointing at what it hides from, over the wall or the crate. Low cover
    /// hides a crouching AI, high cover a standing one. The brain only takes it against a threat it really hides
    /// from (a line from the threat's eyes must be blocked) and that's further from it than the AI is
    /// (<see cref="CoverPicker"/>), so a marker facing the wrong way is never used, just useless. Drawn in the Scene
    /// view: green for high, yellow for low.
    /// </summary>
    public sealed class CoverPoint : MonoBehaviour
    {
        private static readonly List<CoverPoint> s_all = new List<CoverPoint>();

        [Tooltip("Low cover (a wall to the waist, a crate) hides someone crouching; high cover (a wall, a vehicle side-on) someone standing.")]
        [SerializeField] private bool _low;

        /// <summary>Every cover point in the loaded scenes.</summary>
        public static IReadOnlyList<CoverPoint> All => s_all;

        public bool Low => _low;

        public CoverSpot Spot
        {
            get
            {
                var p = transform.position;
                var f = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                return new CoverSpot(new NVector3(p.x, p.y, p.z), new NVector3(f.x, 0f, f.z), _low);
            }
        }

        private void OnEnable() => s_all.Add(this);

        private void OnDisable() => s_all.Remove(this);

        private void OnDrawGizmos()
        {
            var height = _low ? 0.9f : 1.6f;
            Gizmos.color = _low ? new Color(1f, 0.85f, 0.2f) : new Color(0.3f, 0.9f, 0.4f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position + Vector3.up * (height * 0.5f), Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized + Vector3.forward * 0.0001f, Vector3.up), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(0.6f, height, 0.6f));
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 1.2f);
            Gizmos.DrawLine(Vector3.forward * 1.2f, new Vector3(0.2f, 0f, 0.95f));
            Gizmos.DrawLine(Vector3.forward * 1.2f, new Vector3(-0.2f, 0f, 0.95f));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }

    /// <summary>The brain's world, in Unity: the map's <see cref="CoverPoint"/>s, and lines of sight through its colliders (triggers aside).</summary>
    public sealed class UnityAIWorld : IAIWorld
    {
        public static readonly UnityAIWorld Instance = new UnityAIWorld();

        public IEnumerable<CoverSpot> CoverNear(NVector3 point, float radius)
        {
            var centre = new Vector3(point.X, point.Y, point.Z);
            foreach (var cover in CoverPoint.All)
            {
                if (cover != null && Vector3.Distance(cover.transform.position, centre) <= radius)
                {
                    yield return cover.Spot;
                }
            }
        }

        // People don't count: only the map. A player standing in a doorway doesn't make the doorway cover.
        public bool IsBlocked(NVector3 from, NVector3 to)
        {
            var a = new Vector3(from.X, from.Y, from.Z);
            var b = new Vector3(to.X, to.Y, to.Z);
            for (int i = 0; i < 4; i++)
            {
                if (!Physics.Linecast(a, b, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    return false;
                }

                if (hit.collider.GetComponentInParent<IDamageable>() == null)
                {
                    return true;
                }

                a = hit.point + (b - a).normalized * 0.05f; // through the person
            }

            return false;
        }
    }
}
