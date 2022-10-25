using System;

namespace Orleans.Versions.Selector
{
    /// <summary>
    /// Grain interface version selector which allows any compatible version to be chosen.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class AllCompatibleVersions : VersionSelectorStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static AllCompatibleVersions Singleton { get; } = new AllCompatibleVersions();
    }
}