namespace DGSvsHS.Gameplay
{
    public struct DeterministicRng
    {
        private ulong _s0;
        private ulong _s1;

        public static DeterministicRng FromSeed(ulong seed)
        {
            // Seed both state words with SplitMix64, as is standard for xoroshiro.
            ulong sm = seed;
            var rng = new DeterministicRng();
            rng._s0 = SplitMix64(ref sm);
            rng._s1 = SplitMix64(ref sm);
            // Guard against zero state.
            if ((rng._s0 | rng._s1) == 0UL) rng._s1 = 1UL;
            return rng;
        }

        public ulong NextU64()
        {
            ulong s0 = _s0;
            ulong s1 = _s1;
            ulong result = s0 + s1;
            s1 ^= s0;
            _s0 = RotateLeft(s0, 24) ^ s1 ^ (s1 << 16);
            _s1 = RotateLeft(s1, 37);
            return result;
        }
        
        public float NextFloat01()
        {
            return (NextU64() >> 40) * (1.0f / 16777216.0f);
        }
        
        public float NextRange(float min, float max) => min + (max - min) * NextFloat01();

        private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

        private static ulong SplitMix64(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
