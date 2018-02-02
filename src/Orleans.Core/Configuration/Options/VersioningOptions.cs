
using Microsoft.Extensions.Options;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    public class VersioningOptions
    {
        public string DefaultCompatibilityStrategy { get; set; } = DEFAULT_COMPATABILITY_STRATEGY;
        public const string DEFAULT_COMPATABILITY_STRATEGY = nameof(BackwardCompatible);

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
                OptionFormattingUtilities.Format(nameof(options.DefaultCompatibilityStrategy),options.DefaultCompatibilityStrategy),
                OptionFormattingUtilities.Format(nameof(options.DefaultVersionSelectorStrategy),options.DefaultVersionSelectorStrategy),
            };
        }
    }
}
