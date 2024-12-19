using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement.Filtering;

public class RequiredSiloMetadataPlacementFilterStrategy(string[] metadataKeys) : PlacementFilterStrategy
{
    public string[] MetadataKeys { get; private set; } = metadataKeys;

    public RequiredSiloMetadataPlacementFilterStrategy() : this([])
    {
    }

    public override void Initialize(GrainProperties properties)
    {
        base.Initialize(properties);
        MetadataKeys = GetPlacementFilterGrainProperty("metadata-keys", properties).Split(",");
    }

    protected override IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType,
        IReadOnlyDictionary<string, string> existingProperties)
    {
        yield return new KeyValuePair<string, string>("metadata-keys", String.Join(",", MetadataKeys));
    }
}