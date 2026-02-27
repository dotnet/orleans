using Orleans.Placement;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// A placement filter strategy that prefers the local silo for grain placement.
/// When the local silo is among the candidates, it is the only silo returned.
/// Otherwise, all candidate silos are returned unchanged.
/// </summary>
public class PreferLocalPlacementFilterStrategy(int order)
    : PlacementFilterStrategy(order)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreferLocalPlacementFilterStrategy"/> class with default order 0.
    /// </summary>
    public PreferLocalPlacementFilterStrategy() : this(0) { }
}
