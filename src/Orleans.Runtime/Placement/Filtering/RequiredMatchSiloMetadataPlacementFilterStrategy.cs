using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement.Filtering;

public class RequiredMatchSiloMetadataPlacementFilterStrategy(string[] metadataKeys) : PlacementFilterStrategy
{
    public string[] MetadataKeys { get; private set; } = metadataKeys;

    public RequiredMatchSiloMetadataPlacementFilterStrategy() : this([])
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