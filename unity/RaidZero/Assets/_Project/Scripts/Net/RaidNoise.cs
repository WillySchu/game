using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// A noise in the raid, for AI to hear (RaidServer.NoiseMade): where, how far it carries, and who made it.
    /// Gunshots for now (raised by RaidServer.FireRound); footsteps, doors and grenades later.
    /// </summary>
    public struct RaidNoise
    {
        public Vector3 Position;
        public float Radius;

        /// <summary>The player's or AI's ID.</summary>
        public string SourceId;
    }
}
