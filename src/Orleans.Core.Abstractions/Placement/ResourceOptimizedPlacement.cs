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
/// <para><i>This placement strategy is configured by adding the <see cref="Placement.ResourceOptimizedPlacementAttribute"/> attribute to a grain.</i></para>
/// </remarks>
internal sealed class ResourceOptimizedPlacement : PlacementStrategy
{
    internal static readonly ResourceOptimizedPlacement Singleton = new();
}
