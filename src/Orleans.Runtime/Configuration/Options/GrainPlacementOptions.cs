using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Settings which regulate the placement of grains across a cluster.
    /// </summary>
    public class GrainPlacementOptions
    {
        /// <summary>
        /// Default strategy used for placeing grains across a cluster.
        /// </summary>
        public string DefaultPlacementStrategy { get; set; } = DEFAULT_PLACEMENT_STRATEGY;
        public static readonly string DEFAULT_PLACEMENT_STRATEGY = nameof(RandomPlacement);

        /// <summary>
        /// Number of silos randomly selected for consideration when using activation count placement policy.
        /// Only used with Activation Count placement policy.
        /// </summary>
        public int ActivationCountPlacementChooseOutOf { get; set; } = DEFAULT_ACTIVATION_COUNT_PLACEMENT_CHOOSE_OUT_OF;
        public const int DEFAULT_ACTIVATION_COUNT_PLACEMENT_CHOOSE_OUT_OF = 2;
    }

    public class GrainPlacementOptionsFormatter : IOptionFormatter<GrainPlacementOptions>
    {
        public string Category { get; }

        public string Name => nameof(GrainPlacementOptions);

        private GrainPlacementOptions options;
        public GrainPlacementOptionsFormatter(IOptions<GrainPlacementOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.DefaultPlacementStrategy),this.options.DefaultPlacementStrategy),
                OptionFormattingUtilities.Format(nameof(this.options.ActivationCountPlacementChooseOutOf), this.options.ActivationCountPlacementChooseOutOf),
            };
        }
    }
}
