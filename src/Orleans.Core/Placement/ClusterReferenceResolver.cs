using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement;

/// <summary>
/// Resolves universal references to destination clusters.
/// </summary>
public sealed class ClusterReferenceResolver
{
    private readonly ClusterOptions _clusterOptions;
    private readonly MetaclusterOptions _metaclusterOptions;
    private readonly ClusterLocatorResolver _clusterLocatorResolver;
    private readonly GrainPropertiesResolver _grainPropertiesResolver;
    private readonly IMetaclusterTopologyProvider _topologyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<UniversalReference, CacheEntry> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterReferenceResolver"/> class.
    /// </summary>
    public ClusterReferenceResolver(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<MetaclusterOptions> metaclusterOptions,
        ClusterLocatorResolver clusterLocatorResolver,
        GrainPropertiesResolver grainPropertiesResolver,
        IMetaclusterTopologyProvider topologyProvider,
        [FromKeyedServices(TimeProviderNames.SystemTimers)] TimeProvider timeProvider)
    {
        _clusterOptions = clusterOptions.Value;
        _metaclusterOptions = metaclusterOptions.Value;
        _clusterLocatorResolver = clusterLocatorResolver;
        _grainPropertiesResolver = grainPropertiesResolver;
        _topologyProvider = topologyProvider;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Resolves the destination cluster for the provided reference.
    /// </summary>
    public ValueTask<ClusterIdentity> Resolve(
        UniversalReference reference,
        IReadOnlyDictionary<string, object>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        reference.Validate();
        if (!_metaclusterOptions.Enabled
            && reference.Binding == UniversalReferenceBinding.Virtual
            && string.Equals(reference.ServiceId, ClusterOptions.DefaultServiceId, StringComparison.Ordinal))
        {
            return new ValueTask<ClusterIdentity>(
                new ClusterIdentity(_clusterOptions.ServiceId, _clusterOptions.ClusterId));
        }

        if (!string.Equals(reference.ServiceId, _clusterOptions.ServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reference service '{reference.ServiceId}' does not match the local service '{_clusterOptions.ServiceId}'.");
        }

        if (reference.Binding == UniversalReferenceBinding.Cluster)
        {
            var result = new ClusterIdentity(reference.ServiceId, reference.ClusterId!);
            if (!_metaclusterOptions.Enabled || result.ClusterId == _clusterOptions.ClusterId)
            {
                return new ValueTask<ClusterIdentity>(result);
            }

            return ValidateClusterBound(result, cancellationToken);
        }

        if (_clusterLocatorResolver.Resolve(reference.GrainId.Type) is not { } locator)
        {
            return new ValueTask<ClusterIdentity>(new ClusterIdentity(_clusterOptions.ServiceId, _clusterOptions.ClusterId));
        }

        var properties = _grainPropertiesResolver.GetGrainProperties(reference.GrainId.Type);
        var context = new ClusterLocationContext(
            _clusterOptions.ServiceId,
            _clusterOptions.ClusterId,
            properties,
            requestContext);
        if (requestContext is null
            && locator is not IClusterOwnershipValidator
            && _cache.TryGetValue(reference, out var cached)
            && cached.Expiration > _timeProvider.GetUtcNow())
        {
            return ValidateCached(reference, locator, context, cached, cancellationToken);
        }

        var cacheable = requestContext is null && locator is not IClusterOwnershipValidator;
        return ResolveAndCache(reference, locator, context, cacheable, cancellationToken);
    }

    /// <summary>
    /// Invalidates a cached virtual-reference location.
    /// </summary>
    public void Invalidate(UniversalReference reference) => _cache.TryRemove(reference, out _);

    private async ValueTask<ClusterIdentity> ResolveAndCache(
        UniversalReference reference,
        IClusterLocator locator,
        ClusterLocationContext context,
        bool cacheable,
        CancellationToken cancellationToken)
    {
        var grainId = reference.GrainId;
        ClusterLocation location = default;
        MetaclusterTopology topology = default!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            location = await locator.Locate(grainId, context, cancellationToken);
            if (string.IsNullOrWhiteSpace(location.ClusterId))
            {
                throw new InvalidOperationException(
                    $"Cluster locator '{locator.GetType()}' returned an empty cluster identity for grain '{grainId}'.");
            }

            topology = await _topologyProvider.GetTopology(cancellationToken);
            if (!string.Equals(topology.ServiceId, _clusterOptions.ServiceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Topology service '{topology.ServiceId}' does not match local service '{_clusterOptions.ServiceId}'.");
            }

            if (location.IsExistingOwner || location.TopologyEpoch == topology.Epoch)
            {
                break;
            }

            if (attempt == 2)
            {
                throw new InvalidOperationException(
                    $"Cluster topology changed repeatedly while resolving grain '{grainId}'.");
            }
        }

        if (!topology.Clusters.TryGetValue(location.ClusterId, out var cluster)
            || cluster.State == MetaclusterClusterState.Removed
            || (!location.IsExistingOwner && cluster.State != MetaclusterClusterState.Active))
        {
            throw new InvalidOperationException(
                $"Cluster locator '{locator.GetType()}' resolved grain '{grainId}' to unavailable cluster '{location.ClusterId}' at topology epoch '{topology.Epoch}'.");
        }

        var result = new ClusterIdentity(_clusterOptions.ServiceId, location.ClusterId);
        if (cacheable && _metaclusterOptions.ClusterLocationCacheDuration > TimeSpan.Zero)
        {
            _cache[reference] = new CacheEntry(
                result,
                location.Version,
                location.TopologyEpoch,
                _timeProvider.GetUtcNow() + _metaclusterOptions.ClusterLocationCacheDuration);
        }

        return result;
    }

    private async ValueTask<ClusterIdentity> ValidateCached(
        UniversalReference reference,
        IClusterLocator locator,
        ClusterLocationContext context,
        CacheEntry cached,
        CancellationToken cancellationToken)
    {
        var topology = await _topologyProvider.GetTopology(cancellationToken);
        if (topology.Epoch == cached.TopologyEpoch
            && topology.Clusters.TryGetValue(cached.Cluster.ClusterId, out var cluster)
            && cluster.State == MetaclusterClusterState.Active)
        {
            return cached.Cluster;
        }

        _cache.TryRemove(reference, out _);
        return await ResolveAndCache(reference, locator, context, cacheable: true, cancellationToken: cancellationToken);
    }

    private async ValueTask<ClusterIdentity> ValidateClusterBound(
        ClusterIdentity clusterIdentity,
        CancellationToken cancellationToken)
    {
        var topology = await _topologyProvider.GetTopology(cancellationToken);
        if (!string.Equals(topology.ServiceId, clusterIdentity.ServiceId, StringComparison.Ordinal)
            || !topology.Clusters.TryGetValue(clusterIdentity.ClusterId, out var cluster)
            || cluster.State == MetaclusterClusterState.Removed)
        {
            throw new InvalidOperationException(
                $"Cluster-bound reference targets unavailable cluster '{clusterIdentity}' at topology epoch '{topology.Epoch}'.");
        }

        return clusterIdentity;
    }

    private readonly record struct CacheEntry(
        ClusterIdentity Cluster,
        long Version,
        long TopologyEpoch,
        DateTimeOffset Expiration);
}
