using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime;

/// <summary>
/// Resolves the cluster which hosts a virtual Orleans reference.
/// </summary>
public interface IClusterLocator
{
    /// <summary>
    /// Resolves the cluster which hosts the provided grain.
    /// </summary>
    ValueTask<ClusterLocation> Locate(
        GrainId grainId,
        ClusterLocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the cluster which hosts a virtual Orleans reference.
/// </summary>
[GenerateSerializer, Immutable]
public readonly record struct ClusterLocation(
    [property: Id(0)] string ClusterId,
    [property: Id(1)] long Version,
    [property: Id(2)] long TopologyEpoch,
    [property: Id(3)] bool IsExistingOwner);

/// <summary>
/// Provides context for a cluster-location operation.
/// </summary>
public sealed class ClusterLocationContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterLocationContext"/> class.
    /// </summary>
    public ClusterLocationContext(
        string serviceId,
        string localClusterId,
        GrainProperties grainProperties,
        IReadOnlyDictionary<string, object>? requestContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localClusterId);
        ArgumentNullException.ThrowIfNull(grainProperties);

        ServiceId = serviceId;
        LocalClusterId = localClusterId;
        GrainProperties = grainProperties;
        RequestContext = requestContext;
    }

    /// <summary>
    /// Gets the Orleans service identity.
    /// </summary>
    public string ServiceId { get; }

    /// <summary>
    /// Gets the local cluster identity.
    /// </summary>
    public string LocalClusterId { get; }

    /// <summary>
    /// Gets the grain properties.
    /// </summary>
    public GrainProperties GrainProperties { get; }

    /// <summary>
    /// Gets request-specific placement metadata, if present.
    /// </summary>
    public IReadOnlyDictionary<string, object>? RequestContext { get; }
}
