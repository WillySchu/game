using System.Collections.Generic;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// A place players leave the raid from: stand inside the box for <see cref="Seconds"/> and the server
    /// extracts you. Leaving the box cancels the countdown, and taking damage starts it over
    /// (RaidServer.InterruptExtraction).
    ///
    /// The box is centred on this object and turns and scales with it; the green gizmo shows it. It's checked
    /// against each player's object position, so make it tall enough to cover where players stand. No collider
    /// or physics needed.
    /// </summary>
    public sealed class ExtractionZone : MonoBehaviour
    {
        private static readonly List<ExtractionZone> Enabled = new List<ExtractionZone>();

        [Tooltip("The name sent to the backend with the extraction, such as north_gate. Defaults to the object's name.")]
        [SerializeField]
        private string _pointName = "";

        [Tooltip("The box's size, in this object's local units.")]
        [SerializeField]
        private Vector3 _size = new Vector3(6, 3, 6);

        [Tooltip("How long a player must stay inside to extract.")]
        [SerializeField]
        private float _seconds = 8f;

        /// <summary>Every zone in the loaded scenes, for HUD markers.</summary>
        public static IReadOnlyList<ExtractionZone> All => Enabled;

        public string PointName => string.IsNullOrEmpty(_pointName) ? name : _pointName;

        public float Seconds => _seconds;

        /// <summary>The zone the position is inside, or null.</summary>
        public static ExtractionZone At(Vector3 position)
        {
            foreach (var zone in Enabled)
            {
                if (zone.Contains(position))
                {
                    return zone;
                }
            }

            return null;
        }

        public bool Contains(Vector3 position)
        {
            var local = transform.InverseTransformPoint(position);
            return Mathf.Abs(local.x) <= _size.x * 0.5f
                && Mathf.Abs(local.y) <= _size.y * 0.5f
                && Mathf.Abs(local.z) <= _size.z * 0.5f;
        }

        private void OnEnable() => Enabled.Add(this);

        private void OnDisable() => Enabled.Remove(this);

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.2f);
            Gizmos.DrawCube(Vector3.zero, _size);
            Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, _size);
        }
    }
}
