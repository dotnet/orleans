using System;
using System.Diagnostics.CodeAnalysis;
using Orleans.Placement;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Attribute to specify the preferred match silo metadata placement filter that preferentially filters down to silos where the metadata matches the local (calling) silo metadata.
/// </summary>
/// <param name="orderedMetadataKeys">Ordered set of metadata keys to try to match. The earlier entries are considered less important and will be dropped to find a less-specific match if sufficient more-specific matches do not have enough results.</param>
/// <param name="minCandidates">The minimum desired candidates to filter. This is to balance meeting the metadata preferences with not overloading a single or small set of silos with activations. Set this to 1 if you only want the best matches, even if there's only one silo that is currently the best match.</param>
/// <remarks>Example: If keys ["first","second"] are specified, then it will attempt to return only silos where both keys match the local silo's metadata values. If there are not sufficient silos matching both, then it will also include silos matching only the second key. Finally, if there are still fewer than minCandidates results then it will include all silos.</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
[Experimental("ORLEANSEXP004")]
public class PreferredMatchSiloMetadataPlacementFilterAttribute(string[] orderedMetadataKeys, int minCandidates = 2, int order = 0)
    : PlacementFilterAttribute(new PreferredMatchSiloMetadataPlacementFilterStrategy(orderedMetadataKeys, minCandidates, order));