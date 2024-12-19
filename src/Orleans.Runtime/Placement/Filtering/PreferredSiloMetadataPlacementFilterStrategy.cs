using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement.Filtering;

public class PreferredSiloMetadataPlacementFilterStrategy(string[] orderedMetadataKeys) : PlacementFilterStrategy
{
    public string[] OrderedMetadataKeys { get; set; } = orderedMetadataKeys;

    public PreferredSiloMetadataPlacementFilterStrategy() : this([])
    {
    }

    public override void Initialize(GrainProperties properties)
    {
        base.Initialize(properties);
        OrderedMetadataKeys = GetPlacementFilterGrainProperty("ordered-metadata-keys", properties).Split(",");
    }

    protected override IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType,
        IReadOnlyDictionary<string, string> existingProperties)
    {
        yield return new KeyValuePair<string, string>("ordered-metadata-keys", string.Join(",", OrderedMetadataKeys));
    }
}