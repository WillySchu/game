using System;
using System.Collections.Generic;

namespace RaidZero.Raid
{
    /// <summary>
    /// Every player's health in one raid, kept by player rather than on their player object, so a player who
    /// rejoins comes back as hurt as they left. Everyone starts at <see cref="DamageRules.MaxHealth"/>; a new
    /// raid starts a new one of these.
    /// </summary>
    public sealed class RaidHealth
    {
        private readonly Dictionary<string, float> _health = new Dictionary<string, float>(StringComparer.Ordinal);

        public float Max => DamageRules.MaxHealth;

        /// <summary>The player's health: full until they're first hurt.</summary>
        public float Of(string playerId) =>
            playerId != null && _health.TryGetValue(playerId, out var health) ? health : Max;

        public bool IsDead(string playerId) => Of(playerId) <= 0f;

        /// <summary>Takes health off (never below 0) and returns what's left. A negative amount does nothing.</summary>
        public float Damage(string playerId, float amount)
        {
            if (playerId == null)
            {
                throw new ArgumentNullException(nameof(playerId));
            }

            var left = Math.Max(0f, Of(playerId) - Math.Max(0f, amount));
            _health[playerId] = left;
            return left;
        }

        /// <summary>Gives health back (never above <see cref="Max"/>, and not to the dead) and returns the new health.</summary>
        public float Heal(string playerId, float amount)
        {
            if (playerId == null)
            {
                throw new ArgumentNullException(nameof(playerId));
            }

            var health = Of(playerId);
            if (health <= 0f)
            {
                return 0f;
            }

            health = Math.Min(Max, health + Math.Max(0f, amount));
            _health[playerId] = health;
            return health;
        }
    }
}
