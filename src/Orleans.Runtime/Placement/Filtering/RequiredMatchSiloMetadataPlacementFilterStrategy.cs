using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Placement;

namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Configures placement filtering which requires silos to match the calling silo's metadata for every specified key.
/// </summary>
/// <param name="metadataKeys">The metadata keys whose values must match those of the calling silo.</param>
/// <param name="order">The order in which this filter is applied relative to other placement filters.</param>
public class RequiredMatchSiloMetadataPlacementFilterStrategy(string[] metadataKeys, int order)
    : PlacementFilterStrategy(order)
{
    /// <summary>
    /// Gets the metadata keys whose values must match those of the calling silo.
    /// </summary>
    public string[] MetadataKeys { get; private set; } = metadataKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequiredMatchSiloMetadataPlacementFilterStrategy"/> class
    /// with no metadata keys and the default filter order.
    /// </summary>
    public RequiredMatchSiloMetadataPlacementFilterStrategy() : this([], 0)
    {
    }

    /// <inheritdoc />
    public override void AdditionalInitialize(GrainProperties properties)
    {
        var placementFilterGrainProperty = GetPlacementFilterGrainProperty("metadata-keys", properties);
        if (placementFilterGrainProperty is null)
        {
            throw new ArgumentException("Invalid metadata-keys property value.");
        }
        MetadataKeys = placementFilterGrainProperty.Split(",");
    }

    /// <inheritdoc />
    protected override IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType,
        IReadOnlyDictionary<string, string> existingProperties)
    {
        yield return new KeyValuePair<string, string>("metadata-keys", String.Join(",", MetadataKeys));
    }
}