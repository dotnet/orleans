using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class DeploymentLoadStatisticsDisseminationTopic(
    DeploymentLoadPublisher deploymentLoadPublisher,
    IOptionsMonitor<DeploymentLoadPublisherOptions> options,
    Serializer serializer) : IDisseminationTopic
{
    public string Name => DisseminationTopicNames.DeploymentLoad;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.ActiveMembers;

    public DisseminationTopicOptions Options => options.CurrentValue.Dissemination;

    public DisseminationTopicValue CreateValue(SiloAddress origin, SiloRuntimeStatistics statistics)
    {
        var payload = serializer.SerializeToArray(statistics);
        return new DisseminationTopicValue(
            new DisseminationTopicDigest(origin.ToParsableString(), statistics.DateTime.Ticks),
            payload);
    }

    public IReadOnlyList<DisseminationTopicDigest> GetDigests()
    {
        var digests = new List<DisseminationTopicDigest>();
        foreach (var siloAddress in deploymentLoadPublisher.GetActiveSilosForDissemination())
        {
            var version = deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
                          && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress,
                              statistics.DateTime.Ticks)
                ? statistics.DateTime.Ticks
                : long.MinValue;
            digests.Add(new DisseminationTopicDigest(
                siloAddress.ToParsableString(),
                version));
        }

        digests.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return digests;
    }

    public bool IsObsolete(DisseminationTopicDigest digest) =>
        !SiloAddress.TryParse(digest.Key, out var siloAddress)
        || deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, digest.Version);

    public bool TryCreateRepairValue(
        DisseminationTopicDigest localDigest,
        DisseminationTopicDigest peerDigest,
        out DisseminationTopicValue value)
    {
        if (localDigest.Key != peerDigest.Key
            || !SiloAddress.TryParse(localDigest.Key, out var siloAddress)
            || !deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            || statistics.DateTime.Ticks < localDigest.Version)
        {
            value = default;
            return false;
        }

        value = CreateValue(siloAddress, statistics);
        return true;
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationTopicValue value,
        CancellationToken cancellationToken)
    {
        if (!SiloAddress.TryParse(value.Digest.Key, out var siloAddress))
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var statistics = serializer.Deserialize<SiloRuntimeStatistics>(value.Payload);
        return ValueTask.FromResult(
            deploymentLoadPublisher.ApplyDisseminatedRuntimeStatistics(siloAddress, statistics));
    }

    public async ValueTask RecoverAsync(DisseminationTopicDigest digest,
        CancellationToken cancellationToken)
    {
        if (Options.FallbackEnabled && SiloAddress.TryParse(digest.Key, out var siloAddress))
        {
            await deploymentLoadPublisher.RefreshSiloStatisticsForDissemination(siloAddress);
        }
    }
}
