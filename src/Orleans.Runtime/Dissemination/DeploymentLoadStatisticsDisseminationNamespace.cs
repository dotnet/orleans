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

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var activeSilos = deploymentLoadPublisher.GetActiveSilosForStatisticsDigest();
            PruneCache(activeSilos);
            foreach (var siloAddress in activeSilos)
            {
                yield return new DigestEntry(siloAddress, GetVersion(siloAddress));
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

        if (serializer.Deserialize<SiloRuntimeStatistics>(value.Payload) is not { } statistics
            || value.ToVersion != statistics.DateTime.Ticks)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            deploymentLoadPublisher.ApplyDisseminatedRuntimeStatistics(siloAddress, statistics));
    }

    private void PruneCache(IReadOnlyCollection<SiloAddress> activeSilos)
    {
        lock (_cacheLock)
        {
            foreach (var key in _cachedValues.Keys.Where(key => !activeSilos.Contains(key)).ToArray())
            {
                _cachedValues.Remove(key);
            }
        }
    }
}
