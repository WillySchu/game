using System.Collections.Generic;
using RaidZero.Shared.Loot;

namespace RaidZero.Raid
{
    /// <summary>
    /// The dice for spawning AI: how many a spawn zone gets, and which of a profile's kits each one wears. From a
    /// generator seeded by the raid's loot seed and the zone (LootRandom.ForContainer), so the same seed spawns
    /// the same AI with the same gear, and a raid can be replayed.
    /// </summary>
    public static class AISpawnRolls
    {
        /// <summary>How many: evenly between min and max, inclusive. Never negative.</summary>
        public static int Count(LootRandom random, int min, int max)
        {
            min = System.Math.Max(0, min);
            return random.Range(min, System.Math.Max(min, max));
        }

        /// <summary>An index picked in proportion to the weights (0 or less never gets picked), or -1 if none can be.</summary>
        public static int PickWeighted(LootRandom random, IReadOnlyList<int> weights)
        {
            int total = 0;
            for (int i = 0; weights != null && i < weights.Count; i++)
            {
                total += System.Math.Max(0, weights[i]);
            }

            if (total <= 0)
            {
                return -1;
            }

            int roll = random.Next(total);
            for (int i = 0; i < weights.Count; i++)
            {
                roll -= System.Math.Max(0, weights[i]);
                if (roll < 0)
                {
                    return i;
                }
            }

            return weights.Count - 1;
        }
    }
}
