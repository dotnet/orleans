using Microsoft.Extensions.Options;
using Orleans.Runtime;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    public class GrainPlacementOptions
    {
        public string DefaultPlacementStrategy { get; set; } = DEFAULT_PLACEMENT_STRATEGY;
        public static readonly string DEFAULT_PLACEMENT_STRATEGY = nameof(RandomPlacement);

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
                OptionFormattingUtilities.Format(nameof(options.DefaultPlacementStrategy),options.DefaultPlacementStrategy),
                OptionFormattingUtilities.Format(nameof(options.ActivationCountPlacementChooseOutOf), options.ActivationCountPlacementChooseOutOf),
            };
        }
    }
}
