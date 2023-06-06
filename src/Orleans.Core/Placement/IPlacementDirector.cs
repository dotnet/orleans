using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Interface for placement directors.
    /// </summary>
    public interface IPlacementDirector
    {
        /// <summary>
        /// Gets the <see cref="PlacementTarget.RequestContextData"/> key used to store the placement hint, if present.
        /// </summary>
        public const string PlacementHintKey = nameof(PlacementHintKey);

        /// <summary>
        /// Picks an appropriate silo to place the specified target on.
        /// </summary>
        /// <param name="strategy">The target's placement strategy.</param>
        /// <param name="target">The grain being placed as well as information about the request which triggered the placement.</param>
        /// <param name="context">The placement context.</param>
        /// <returns>An appropriate silo to place the specified target on.</returns>
        Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context);

        /// <summary>
        /// Gets the placement hint from the provided request context data, if present and valid.
        /// </summary>
        /// <param name="requestContextData">The request context data.</param>
        /// <param name="compatibleSilos">The compatible silos.</param>
        /// <returns>The placement hint, if present and valid, or <see landword="null"/> otherwise.</returns>
        public static SiloAddress GetPlacementHint(Dictionary<string, object> requestContextData, SiloAddress[] compatibleSilos)
        {
            if (requestContextData is { Count: > 0 } data
                && data.TryGetValue(PlacementHintKey, out var value)
                && value is SiloAddress placementHint
                && compatibleSilos.Contains(placementHint))
            {
                return placementHint;
            }

            return null;
        }
    }
}
