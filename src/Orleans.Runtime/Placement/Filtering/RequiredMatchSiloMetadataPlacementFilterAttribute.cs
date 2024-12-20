using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Attribute to specify that a silo must have a specific metadata key-value pair matching the local (calling) silo to be considered for placement.
/// </summary>
/// <param name="metadataKeys"></param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
[Experimental("ORLEANSEXP004")]
public class RequiredMatchSiloMetadataPlacementFilterAttribute(string[] metadataKeys)
    : PlacementFilterAttribute(new RequiredMatchSiloMetadataPlacementFilterStrategy(metadataKeys));