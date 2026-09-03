using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;
using Orleans.Placement;

namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Configures placement filtering which prefers silos whose metadata matches the calling silo's metadata.
/// </summary>
/// <param name="orderedMetadataKeys">
/// The metadata keys to match, ordered from least to most important. Less important keys are omitted progressively
/// until at least <paramref name="minCandidates"/> candidates remain.
/// </param>
/// <param name="minCandidates">The desired minimum number of placement candidates.</param>
/// <param name="order">The order in which this filter is applied relative to other placement filters.</param>
public class PreferredMatchSiloMetadataPlacementFilterStrategy(string[] orderedMetadataKeys, int minCandidates, int order)
    : PlacementFilterStrategy(order)
{
    /// <summary>
    /// Gets or sets the metadata keys to match, ordered from least to most important.
    /// </summary>
    public string[] OrderedMetadataKeys { get; set; } = orderedMetadataKeys;

    /// <summary>
    /// Gets or sets the desired minimum number of placement candidates.
    /// </summary>
    public int MinCandidates { get; set; } = minCandidates;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferredMatchSiloMetadataPlacementFilterStrategy"/> class
    /// with no metadata keys, a minimum of two candidates, and the default filter order.
    /// </summary>
    public PreferredMatchSiloMetadataPlacementFilterStrategy() : this([], 2, 0)
    {
    }

    /// <inheritdoc />
    public override void AdditionalInitialize(GrainProperties properties)
    {
        var placementFilterGrainProperty = GetPlacementFilterGrainProperty("ordered-metadata-keys", properties);
        if (placementFilterGrainProperty is null)
        {
            throw new ArgumentException("Invalid ordered-metadata-keys property value.");
        }

        OrderedMetadataKeys = placementFilterGrainProperty.Split(",");
        var minCandidatesProperty = GetPlacementFilterGrainProperty("min-candidates", properties);
        if (!int.TryParse(minCandidatesProperty, out var parsedMinCandidates))
        {
            throw new ArgumentException("Invalid min-candidates property value.");
        }

        MinCandidates = parsedMinCandidates;
    }

    /// <inheritdoc />
    protected override IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType,
        IReadOnlyDictionary<string, string> existingProperties)
    {
        yield return new KeyValuePair<string, string>("ordered-metadata-keys", string.Join(",", OrderedMetadataKeys));
        yield return new KeyValuePair<string, string>("min-candidates", MinCandidates.ToString(CultureInfo.InvariantCulture));
    }
}