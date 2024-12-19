using System;

namespace Orleans.Runtime.Placement.Filtering;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class PreferredSiloMetadataPlacementFilterAttribute(string[] orderedMetadataKeys)
    : PlacementFilterAttribute(new PreferredSiloMetadataPlacementFilterStrategy(orderedMetadataKeys));