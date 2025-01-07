using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement.Filtering;

public class PreferredMatchSiloMetadataPlacementFilterStrategy(string[] orderedMetadataKeys, int minCandidates, int order)
    : PlacementFilterStrategy(order)
{
    public string[] OrderedMetadataKeys { get; set; } = orderedMetadataKeys;
    public int MinCandidates { get; set; } = minCandidates;

    public PreferredMatchSiloMetadataPlacementFilterStrategy() : this([], 1, 0)
    {
    }

    public override void AdditionalInitialize(GrainProperties properties)
    {
        OrderedMetadataKeys = GetPlacementFilterGrainProperty("ordered-metadata-keys", properties).Split(",");
        var minCandidatesProperty = GetPlacementFilterGrainProperty("min-candidates", properties);
        if (!int.TryParse(minCandidatesProperty, out var parsedMinCandidates))
        {
            throw new ArgumentException("Invalid min-candidates property value.");
        }

        MinCandidates = parsedMinCandidates;
    }

    protected override IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType,
        IReadOnlyDictionary<string, string> existingProperties)
    {
        yield return new KeyValuePair<string, string>("ordered-metadata-keys", string.Join(",", OrderedMetadataKeys));
        yield return new KeyValuePair<string, string>("min-candidates", MinCandidates.ToString());
    }
}