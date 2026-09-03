using System;
using System.Diagnostics.CodeAnalysis;
using Orleans.Placement;

namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Attribute to specify that a silo must have a specific metadata key-value pair matching the local (calling) silo to be considered for placement.
/// </summary>
/// <param name="metadataKeys">The metadata keys whose values must match those of the calling silo.</param>
/// <param name="order">The order in which this filter is applied relative to other placement filters.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
[Experimental("ORLEANSEXP004")]
public class RequiredMatchSiloMetadataPlacementFilterAttribute(string[] metadataKeys, int order = 0)
    : PlacementFilterAttribute(new RequiredMatchSiloMetadataPlacementFilterStrategy(metadataKeys, order));