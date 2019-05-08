using FASTER.core;

namespace Grains.Models
{
    public class LookupItemFasterKeyComparer : IFasterEqualityComparer<int>
    {
        public bool Equals(ref int k1, ref int k2) => k1 == k2;

        public long GetHashCode64(ref int k) => k.GetHashCode();

        public static LookupItemFasterKeyComparer Default { get; } = new LookupItemFasterKeyComparer();
    }
}
