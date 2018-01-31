using Orleans.Runtime;

namespace Orleans.Hosting
{
    public class GrainPlacementOptions
    {
        public string DefaultPlacementStrategy { get; set; } = DEFAULT_PLACEMENT_STRATEGY;
        public static readonly string DEFAULT_PLACEMENT_STRATEGY = nameof(RandomPlacement);

        public int ActivationCountPlacementChooseOutOf { get; set; } = DEFAULT_ACTIVATION_COUNT_PLACEMENT_CHOOSE_OUT_OF;
        public const int DEFAULT_ACTIVATION_COUNT_PLACEMENT_CHOOSE_OUT_OF = 2;
    }
}
