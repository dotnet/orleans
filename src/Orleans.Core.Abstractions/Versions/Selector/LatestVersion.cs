using System;

namespace Orleans.Versions.Selector
{
    [Serializable]
    public class LatestVersion : VersionSelectorStrategy
    {
        public static LatestVersion Singleton { get; } = new LatestVersion();
    }
}