using System;

namespace Orleans.Versions.Selector
{
    /// <summary>
    /// Grain interface version selector which always selects the highest compatible version.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class LatestVersion : VersionSelectorStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static LatestVersion Singleton { get; } = new LatestVersion();
    }
}