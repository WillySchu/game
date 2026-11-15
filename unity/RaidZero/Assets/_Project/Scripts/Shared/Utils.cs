using UnityEngine;

namespace RaidZero.Shared
{
    public static class Utils
    {
        public struct SyncedRandom
        {
            private uint _baseSeed;
            private uint _state;
            public SyncedRandom(uint seed) { _baseSeed = seed == 0 ? 0x9E3779B9u : seed; _state = _baseSeed; }

            // Sync the random generator's state based on current tick.
            public void Sync(uint tick)
            {
                _state = CombineMix(_baseSeed, tick);
            }

            public uint Next()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return _state;
            }

            // Next float in range [0, 1).
            public float NextFloat() => (Next() >> 8) * (1f / (1 << 24));
            public float NextFloatRange(float min, float max) => min + NextFloat() * (max - min);
            
        }

        public static uint CombineMix(uint a, uint b)
        {
            a ^= b * 0x9E3779B9u;
            a ^= a >> 16; a *= 0x85EBCACBu;
            a ^= a >> 13; a *= 0xC2B2AE3Du;
            a ^= a >> 16;
            return a;
        }
    
    }
}
