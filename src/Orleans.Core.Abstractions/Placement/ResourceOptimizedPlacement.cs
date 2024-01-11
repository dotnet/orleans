namespace Orleans.Runtime;

/// <summary>
/// A placement strategy which attempts to optimize resource distribution across the cluster.
/// </summary>
/// <remarks>
/// <para>It assigns weights to runtime statistics to prioritize different resources and calculates a normalized score for each silo.
/// The silo with the lowest score is chosen for placing the activation. Normalization ensures that each property contributes proportionally
/// to the overall score. You can adjust the weights based on your specific requirements and priorities for load balancing.
/// In addition to normalization, an <a href="https://en.wikipedia.org/wiki/Online_algorithm">online</a> <a href="https://en.wikipedia.org/wiki/Adaptive_algorithm">adaptive</a>
/// algorithm provides a smoothing effect (filters out high frequency components) and avoids rapid signal drops by transforming it into a polynomial alike decay process.
/// This contributes to avoiding resource saturation on the silos and especially newly joined silos.</para>
/// <para>Silos which are overloaded by definition of the load shedding mechanism are not considered as candidates for new placements.</para>
/// <para><i>This placement strategy is configured by adding the <see cref="Placement.ResourceOptimizedPlacementAttribute"/> attribute to a grain.</i></para>
/// </remarks>
public sealed class ResourceOptimizedPlacement : PlacementStrategy
{
    internal static readonly ResourceOptimizedPlacement Singleton = new();
}
