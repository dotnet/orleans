using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    public class AllVersionsCompatible : CompatibilityStrategy
    {
        public static AllVersionsCompatible Singleton { get; } = new AllVersionsCompatible();
    }
}
