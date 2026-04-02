namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// FNV-1a (Fowler–Noll–Vo) 32-bit hash — deterministic across all .NET versions and platforms.
    /// Used to derive stable item type IDs from string keys so host and client agree on the same
    /// type numbers even when they have different subsets of mods installed.
    /// </summary>
    internal static class FNV1a
    {
        private const uint OffsetBasis = 2166136261u;
        private const uint Prime = 16777619u;

        /// <summary>
        /// Compute FNV-1a 32-bit hash of a UTF-16 string (byte-by-byte over each char's two bytes).
        /// Lowercase-invariant: the input is lowercased before hashing to match OrdinalIgnoreCase
        /// used elsewhere in ItemRegistry.
        /// </summary>
        public static uint Hash(string s)
        {
            uint hash = OffsetBasis;
            if (s == null) return hash;

            foreach (char c in s)
            {
                char lower = char.ToLowerInvariant(c);
                byte lo = (byte)(lower & 0xFF);
                byte hi = (byte)((lower >> 8) & 0xFF);
                hash = (hash ^ lo) * Prime;
                hash = (hash ^ hi) * Prime;
            }

            return hash;
        }

        /// <summary>
        /// Map a hash to an integer in [minInclusive, maxExclusive).
        /// </summary>
        public static int ToRange(uint hash, int minInclusive, int maxExclusive)
        {
            int range = maxExclusive - minInclusive;
            return minInclusive + (int)(hash % (uint)range);
        }
    }
}
