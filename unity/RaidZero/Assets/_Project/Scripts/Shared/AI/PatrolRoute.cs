using System.Collections.Generic;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// A route AI walk between posts: its child objects, in order, are the points. Contractors' spawn zones name
    /// one; the brain walks it (it does nothing on its own yet). Drawn in the Scene view.
    /// </summary>
    public sealed class PatrolRoute : MonoBehaviour
    {
        [Tooltip("Back to the first point after the last, or back along the route.")]
        [SerializeField] private bool _loop = true;

        public bool Loop => _loop;

        /// <summary>The points, in order, where they are now.</summary>
        public IReadOnlyList<Vector3> Points
        {
            get
            {
                var points = new List<Vector3>(transform.childCount);
                foreach (Transform point in transform)
                {
                    points.Add(point.position);
                }

                return points;
            }
        }

        private void OnDrawGizmos()
        {
            var points = Points;
            Gizmos.color = new Color(0.35f, 0.65f, 1f);
            for (int i = 0; i < points.Count; i++)
            {
                Gizmos.DrawWireSphere(points[i], 0.3f);
                if (i + 1 < points.Count)
                {
                    Gizmos.DrawLine(points[i], points[i + 1]);
                }
            }

            if (_loop && points.Count > 2)
            {
                Gizmos.DrawLine(points[points.Count - 1], points[0]);
            }
        }
    }
}
