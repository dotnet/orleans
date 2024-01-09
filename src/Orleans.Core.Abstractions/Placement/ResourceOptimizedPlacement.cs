namespace Orleans.Runtime;

/// <summary>
/// A placement strategy which attempts to achieve approximately even load based on cluster resources.
/// </summary>
/// <remarks>
/// <para>It assigns weights to runtime statistics to prioritize different properties and calculates a normalized score for each silo.
/// The silo with the highest score is chosen for placing the activation. 
/// Normalization ensures that each property contributes proportionally to the overall score.
/// You can adjust the weights based on your specific requirements and priorities for load balancing.
/// In addition to normalization, an online adaptive filter provides a smoothing effect 
/// (filters out high frequency components) and avoids rapid signal drops by transforming it into a polynomial alike decay process.
/// This contributes to avoiding resource saturation on the silos and especially newly joined silos.</para>
/// <para>Details of the properties used to make the placement decisions and their default values are given below:
/// <list type="number">
/// <item><b>Cpu usage:</b> The default weight (0.3), indicates that CPU usage is important but not the sole determinant in placement decisions.</item>
/// <item><b>Available memory:</b> The default weight (0.4), emphasizes the importance of nodes with ample available memory.</item>
/// <item><b>Memory usage:</b> Is important for understanding the current load on a node. The default weight (0.2), ensures consideration without making it overly influential.</item>
/// <item><b>Total physical memory:</b> Represents the overall capacity of a node. The default weight (0.1), contributes to a more long-term resource planning perspective.</item>
/// </list></para>
/// <para><i>This placement strategy is configured by adding the <see cref="Placement.ResourceOptimizedPlacementAttribute"/> attribute to a grain.</i></para>
/// </remarks>
[Immutable, GenerateSerializer, SuppressReferenceTracking]
internal sealed class ResourceOptimizedPlacement : PlacementStrategy
{
    internal static readonly ResourceOptimizedPlacement Singleton = new();
}
