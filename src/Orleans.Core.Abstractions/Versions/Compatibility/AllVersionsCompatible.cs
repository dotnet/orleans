using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    public class AllVersionsCompatible : CompatibilityStrategy
    {
        public static AllVersionsCompatible Singleton { get; } = new AllVersionsCompatible();

        private AllVersionsCompatible()
        { }

        public override bool Equals(object obj)
        {
            return obj is AllVersionsCompatible;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
