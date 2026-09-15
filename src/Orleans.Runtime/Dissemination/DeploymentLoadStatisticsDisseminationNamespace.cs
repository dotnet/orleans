using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

// A newer load sample supersedes every older sample, so this namespace never needs a version chain.
internal sealed class DeploymentLoadStatisticsDisseminationNamespace(
    DeploymentLoadPublisher deploymentLoadPublisher,
    IOptionsMonitor<DeploymentLoadPublisherOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<SiloAddress, DisseminationValue> _cachedValues = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.DeploymentLoad;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.ActiveMembers;

    public DisseminationNamespaceOptions Options => options.CurrentValue.Dissemination;

    public DisseminationValue CreateValue(SiloAddress origin, SiloRuntimeStatistics statistics)
    {
        lock (_cacheLock)
        {
            // Reuse serialization until this silo publishes a new timestamp.
            if (_cachedValues.TryGetValue(origin, out var cached)
                && cached.ToVersion == statistics.DateTime.Ticks)
            {
                return cached;
            }

            var result = new DisseminationValue(
                origin,
                fromVersion: 0,
                toVersion: statistics.DateTime.Ticks,
                serializer.SerializeToArray(statistics));
            _cachedValues[origin] = result;
            return result;
        }
    }

    public IEnumerable<DisseminationKey> Keys
    {
        get
        {
            var activeSilos = deploymentLoadPublisher.GetActiveSiloStatusesForStatisticsDigest();
            PruneCache(activeSilos);
            foreach (var siloAddress in activeSilos.Keys)
            {
                yield return siloAddress;
            }
        }
    }

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            foreach (var key in Keys)
            {
                yield return new DigestEntry(key, GetVersion(key));
            }
        }
    }

    public long GetVersion(DisseminationKey key) =>
        key.Value is SiloAddress siloAddress
            && deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, statistics.DateTime.Ticks)
                ? statistics.DateTime.Ticks
                : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key.Value is not SiloAddress siloAddress
            || !deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            || deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, statistics.DateTime.Ticks))
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        var version = statistics.DateTime.Ticks;
        if (request.ToVersion is { } targetVersion && targetVersion != version)
        {
            return DisseminationRepairResult.Unavailable(version);
        }

        if (request.FromVersion is { } peerVersion && peerVersion >= version)
        {
            return DisseminationRepairResult.Current(version);
        }

        if (request.MaxItemCount <= 0)
        {
            return DisseminationRepairResult.InsufficientCapacity(version);
        }

        // Every repair is a full value from zero, making the peer's exact baseline irrelevant.
        var value = CreateValue(siloAddress, statistics);
        return value.Payload.Length <= request.MaxPayloadBytes
            && value.Payload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(version, [value])
                : DisseminationRepairResult.InsufficientCapacity(version);
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.Key.Value is not SiloAddress siloAddress)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        if (value.FromVersion != 0
            || serializer.Deserialize<SiloRuntimeStatistics>(value.Payload) is not { } statistics
            || value.ToVersion != statistics.DateTime.Ticks)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ApplyAndCache(siloAddress, statistics, value, cancellationToken);
    }

    private async ValueTask<DisseminationApplyResult> ApplyAndCache(
        SiloAddress origin,
        SiloRuntimeStatistics statistics,
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        var result = await deploymentLoadPublisher.ApplyDisseminatedRuntimeStatisticsAsync(origin, statistics, cancellationToken);
        if (result is DisseminationApplyResult.Applied)
        {
            lock (_cacheLock)
            {
                // The owner accepted these exact bytes. Forward them without serializing the same sample again.
                if (!_cachedValues.TryGetValue(origin, out var cached) || cached.ToVersion < value.ToVersion)
                {
                    _cachedValues[origin] = value;
                }
            }
        }

        return result;
    }

    private void PruneCache(Dictionary<SiloAddress, SiloStatus> activeSilos)
    {
        lock (_cacheLock)
        {
            foreach (var key in _cachedValues.Keys)
            {
                if (!activeSilos.ContainsKey(key))
                {
                    _cachedValues.Remove(key);
                }
            }
        }
    }
}
