using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    public class StrictVersionCompatible : CompatibilityStrategy
    {
        public static StrictVersionCompatible Singleton { get; } = new StrictVersionCompatible();
    }
}