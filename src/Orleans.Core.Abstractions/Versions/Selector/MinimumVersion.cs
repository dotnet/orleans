using System;

namespace Orleans.Versions.Selector
{
    [Serializable]
    [GenerateSerializer]
    public class MinimumVersion : VersionSelectorStrategy
    {
        public static MinimumVersion Singleton { get; } = new MinimumVersion();
    }
}