using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class DeploymentLoadStatisticsDisseminationNamespace(
    DeploymentLoadPublisher deploymentLoadPublisher,
    IOptionsMonitor<DeploymentLoadPublisherOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    public string Name => DisseminationNamespaceNames.DeploymentLoad;

    public DisseminationGroup Group => DisseminationGroup.ActiveMembers;

    public DisseminationNamespaceOptions Options => options.CurrentValue.Dissemination;

    public DisseminationValue CreateValue(SiloAddress origin, SiloRuntimeStatistics statistics)
    {
        var payload = serializer.SerializeToArray(statistics);
        return new DisseminationValue(
            origin.ToParsableString(),
            fromVersion: 0,
            toVersion: statistics.DateTime.Ticks,
            payload);
    }

    public IReadOnlyDictionary<string, long> GetDigest()
    {
        var digest = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var siloAddress in deploymentLoadPublisher.GetActiveSilosForDissemination())
        {
            var version = deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
                          && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress,
                              statistics.DateTime.Ticks)
                ? statistics.DateTime.Ticks
                : 0;
            digest[siloAddress.ToParsableString()] = version;
        }

        return digest;
    }

    public long GetVersion(string key) =>
        SiloAddress.TryParse(key, out var siloAddress)
            && deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, statistics.DateTime.Ticks)
                ? statistics.DateTime.Ticks
                : 0;

    public bool TryCreateRepairValue(
        string key,
        long peerVersion,
        out DisseminationValue value)
    {
        if (!SiloAddress.TryParse(key, out var siloAddress)
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
        if (!SiloAddress.TryParse(value.Key, out var siloAddress))
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
