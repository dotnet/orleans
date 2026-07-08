using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class DeploymentLoadStatisticsDisseminationNamespace(
    DeploymentLoadPublisher deploymentLoadPublisher,
    IOptionsMonitor<DeploymentLoadPublisherOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    public DisseminationNamespace Name => DisseminationNamespaceNames.DeploymentLoad;

    public DisseminationGroup Group => DisseminationGroup.ActiveMembers;

    public DisseminationNamespaceOptions Options => options.CurrentValue.Dissemination;

    public DisseminationValue CreateValue(SiloAddress origin, SiloRuntimeStatistics statistics)
    {
        var payload = serializer.SerializeToArray(statistics);
        return new DisseminationValue(
            origin,
            fromVersion: 0,
            toVersion: statistics.DateTime.Ticks,
            payload);
    }

    public IReadOnlyDictionary<DisseminationKey, long> GetDigest()
    {
        var digest = new Dictionary<DisseminationKey, long>();
        foreach (var siloAddress in deploymentLoadPublisher.GetActiveSilosForDissemination())
        {
            var version = deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
                          && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress,
                              statistics.DateTime.Ticks)
                ? statistics.DateTime.Ticks
                : 0;
            digest[siloAddress] = version;
        }

        return digest;
    }

    public long GetVersion(DisseminationKey key) =>
        key.Value is SiloAddress siloAddress
            && deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, statistics.DateTime.Ticks)
                ? statistics.DateTime.Ticks
                : 0;

    public bool TryCreateRepairValue(
        DisseminationKey key,
        long peerVersion,
        out DisseminationValue value)
    {
        if (key.Value is not SiloAddress siloAddress
            || !deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            || statistics.DateTime.Ticks <= peerVersion)
        {
            value = default;
            return false;
        }

        value = CreateValue(siloAddress, statistics);
        return true;
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key.Value is not SiloAddress siloAddress)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var statistics = serializer.Deserialize<SiloRuntimeStatistics>(value.Payload);
        if (value.ToVersion != statistics.DateTime.Ticks)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        return ValueTask.FromResult(
            deploymentLoadPublisher.ApplyDisseminatedRuntimeStatistics(siloAddress, statistics));
    }

}
