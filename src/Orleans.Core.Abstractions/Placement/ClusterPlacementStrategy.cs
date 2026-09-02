using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime;

/// <summary>
/// Describes how candidate clusters are selected when a cluster locator needs to establish or reconsider a location.
/// </summary>
[Serializable, SerializerTransparent]
public abstract class ClusterPlacementStrategy
{
    /// <summary>
    /// Initializes this strategy using the provided grain properties.
    /// </summary>
    public virtual void Initialize(GrainProperties properties)
    {
    }

    /// <summary>
    /// Populates grain properties for this strategy.
    /// </summary>
    public virtual void PopulateGrainProperties(
        IServiceProvider services,
        Type grainClass,
        GrainType grainType,
        Dictionary<string, string> properties)
        => properties[WellKnownGrainTypeProperties.ClusterPlacementStrategy] = GetType().Name;
}

/// <summary>
/// Selects candidate clusters for a cluster placement strategy.
/// </summary>
public interface IClusterPlacementDirector
{
    /// <summary>
    /// Selects candidate clusters for the provided grain.
    /// </summary>
    ValueTask<ClusterPlacementResult> SelectClusters(
        ClusterPlacementStrategy strategy,
        GrainId grainId,
        ClusterLocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Contains candidate clusters in preference order.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record ClusterPlacementResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterPlacementResult"/> class.
    /// </summary>
    public ClusterPlacementResult(IEnumerable<string> candidateClusters)
    {
        ArgumentNullException.ThrowIfNull(candidateClusters);
        CandidateClusters = candidateClusters.ToImmutableArray();
        if (CandidateClusters.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Candidate cluster identities must be non-empty.", nameof(candidateClusters));
        }
    }

    /// <summary>
    /// Gets candidate clusters in preference order.
    /// </summary>
    [Id(0)]
    public ImmutableArray<string> CandidateClusters { get; }
}
