using System.Diagnostics.CodeAnalysis;
using Orleans.Placement;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Attribute to specify that the local silo should be preferred for grain placement.
/// When the local silo is among the compatible candidates, only the local silo is returned.
/// Otherwise, all candidate silos are returned for the configured placement strategy to choose from.
/// </summary>
/// <param name="order">The order in which this filter is applied relative to other placement filters.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
[Experimental("ORLEANSEXP004")]
public class PreferLocalPlacementFilterAttribute(int order = 0)
    : PlacementFilterAttribute(new PreferLocalPlacementFilterStrategy(order));
