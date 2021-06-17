using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    [GenerateSerializer]
    public class BackwardCompatible : CompatibilityStrategy
    {
        public static BackwardCompatible Singleton { get; } = new BackwardCompatible();
    }
}