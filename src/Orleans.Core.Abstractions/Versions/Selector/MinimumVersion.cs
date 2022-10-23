using System;

namespace Orleans.Versions.Selector
{
    /// <summary>
    /// Grain interface version selector which always selects the lowest compatible version.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class MinimumVersion : VersionSelectorStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static MinimumVersion Singleton { get; } = new MinimumVersion();
    }
}