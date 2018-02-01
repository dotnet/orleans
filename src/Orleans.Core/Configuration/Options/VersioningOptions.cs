
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Hosting
{
    public class VersioningOptions
    {
        public string DefaultCompatibilityStrategy { get; set; } = DEFAULT_COMPATABILITY_STRATEGY;
        public const string DEFAULT_COMPATABILITY_STRATEGY = nameof(BackwardCompatible);

        public string DefaultVersionSelectorStrategy { get; set; } = DEFAULT_VERSION_SELECTOR_STRATEGY;
        public const string DEFAULT_VERSION_SELECTOR_STRATEGY = nameof(AllCompatibleVersions);
    }
}
