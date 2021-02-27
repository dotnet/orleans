using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    [GenerateSerializer]
    public class AllVersionsCompatible : CompatibilityStrategy
    {
        public static AllVersionsCompatible Singleton { get; } = new AllVersionsCompatible();
    }
}
