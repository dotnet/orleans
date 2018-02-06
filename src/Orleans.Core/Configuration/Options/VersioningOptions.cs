
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Hosting
{
    /// <summary>
    /// Versioning options govern grain implementation selection in heterogeneous deployments.
    /// </summary>
    public class VersioningOptions
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

    public class VersioningOptionsFormatter : IOptionFormatter<VersioningOptions>
    {
        public string Category { get; }

        public string Name => nameof(VersioningOptions);

        private VersioningOptions options;
        public VersioningOptionsFormatter(IOptions<VersioningOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.DefaultCompatibilityStrategy),this.options.DefaultCompatibilityStrategy),
                OptionFormattingUtilities.Format(nameof(this.options.DefaultVersionSelectorStrategy),this.options.DefaultVersionSelectorStrategy),
            };
        }
    }
}
