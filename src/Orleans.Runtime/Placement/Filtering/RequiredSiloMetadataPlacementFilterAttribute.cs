using System;

namespace Orleans.Runtime.Placement.Filtering;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class RequiredSiloMetadataPlacementFilterAttribute(string[] orderedMetadataKeys)
    : PlacementFilterAttribute(new RequiredSiloMetadataPlacementFilterStrategy(orderedMetadataKeys));