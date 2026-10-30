using System;
using System.Text;

namespace RaidZero.Shared.Loot
{
    /// <summary>
    /// A small seeded random number generator (SplitMix64) that gives the same numbers everywhere: the game
    /// server under Unity, the editor's simulator and the tests. System.Random isn't promised to do that across
    /// runtimes. Each container gets its own, from the raid's seed and the container's ID, so a raid's loot can
    /// be rolled again exactly from its seed, and one crate's contents don't depend on the others'.
    /// </summary>
    public sealed class LootRandom
    {
        private ulong _state;

        public LootRandom(ulong seed)
        {
            _state = seed;
        }

        /// <summary>The generator for one container in one raid.</summary>
        public static LootRandom ForContainer(int raidSeed, string containerId)
        {
            // FNV-1a over the ID's bytes, then mixed with the seed.
            ulong hash = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes(containerId ?? ""))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }

            return new LootRandom(hash ^ ((ulong)(uint)raidSeed * 0x9E3779B97F4A7C15UL));
        }

        public ulong NextULong()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>0 up to but not including <paramref name="maxExclusive"/>.</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Needs to be at least 1.");
            }

            return (int)(NextULong() % (ulong)maxExclusive);
        }

        /// <summary>Between the two, both included.</summary>
        public int Range(int min, int max) => max <= min ? min : min + Next(max - min + 1);

        /// <summary>0 (included) to 1 (not included).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public double Range(double min, double max) => max <= min ? min : min + NextDouble() * (max - min);
    }
}
