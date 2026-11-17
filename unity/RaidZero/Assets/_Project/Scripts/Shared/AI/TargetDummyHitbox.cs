using FishNet.Connection;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// A collider on a <see cref="TargetDummy"/> that's a body part other than its torso (its head): shots that hit
    /// it count as hitting there. The pattern the player's and AI's hitboxes can follow.
    /// </summary>
    public sealed class TargetDummyHitbox : MonoBehaviour, IDamageable
    {
        [SerializeField] private BodyPart _part = BodyPart.Head;

        public BodyPart Part
        {
            get => _part;
            set => _part = value;
        }

        public void Hit(NetworkConnection source, string ammoDefID)
        {
            var dummy = GetComponentInParent<TargetDummy>();
            if (dummy != null)
            {
                dummy.Hit(source, ammoDefID, _part);
            }
        }
    }
}
