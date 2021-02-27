using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    [GenerateSerializer]
    public class StrictVersionCompatible : CompatibilityStrategy
    {
        public static StrictVersionCompatible Singleton { get; } = new StrictVersionCompatible();
    }
}