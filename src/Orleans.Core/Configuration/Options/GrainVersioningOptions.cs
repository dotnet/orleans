
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Configuration
{
    /// <summary>
    /// Versioning options govern grain implementation selection in heterogeneous deployments.
    /// </summary>
    public class GrainVersioningOptions
    {
        /// <summary>
        /// Strategy used to determine grain compatibility in heterogeneous deployments.
        /// </summary>
        public string DefaultCompatibilityStrategy { get; set; } = DEFAULT_COMPATABILITY_STRATEGY;
        public const string DEFAULT_COMPATABILITY_STRATEGY = nameof(BackwardCompatible);

        /// <summary>
        /// Strategy for selecting grain versions in heterogeneous deployments.
        /// </summary>
        public string DefaultVersionSelectorStrategy { get; set; } = DEFAULT_VERSION_SELECTOR_STRATEGY;
        public const string DEFAULT_VERSION_SELECTOR_STRATEGY = nameof(AllCompatibleVersions);
    }
}
