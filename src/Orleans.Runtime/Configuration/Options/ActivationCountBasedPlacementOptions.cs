using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Settings which regulate the placement of grains across a cluster when using <see cref="ActivationCountBasedPlacement"/>.
    /// </summary>
    public class ActivationCountBasedPlacementOptions
    {
        /// <summary>
        /// Gets or sets the number of silos randomly selected for consideration when using activation count placement policy.
        /// Only used with Activation Count placement policy.
        /// </summary>
        public int ChooseOutOf { get; set; } = DEFAULT_ACTIVATION_COUNT_PLACEMENT_CHOOSE_OUT_OF;

        /// <summary>
        /// The default number of silos to choose from when making placement decisions.
        /// </summary>
        public const int DEFAULT_ACTIVATION_COUNT_PLACEMENT_CHOOSE_OUT_OF = 2;
    }

    /// <summary>
    /// Validates <see cref="ActivationCountBasedPlacementOptions"/> properties.
    /// </summary>
    internal class ActivationCountBasedPlacementOptionsValidator : IConfigurationValidator
    {
        private readonly ActivationCountBasedPlacementOptions options;

        public ActivationCountBasedPlacementOptionsValidator(IOptions<ActivationCountBasedPlacementOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (this.options.ChooseOutOf <= 0)
            {
                throw new OrleansConfigurationException(
                    $"The value of {nameof(ActivationCountBasedPlacementOptions)}.{nameof(this.options.ChooseOutOf)} must be greater than 0.");
            }
        }
    }
}