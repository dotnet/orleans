using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class DeploymentLoadStatisticsDisseminationTopic(
    DeploymentLoadPublisher deploymentLoadPublisher,
    IOptionsMonitor<DeploymentLoadPublisherOptions> options,
    Serializer serializer,
    TimeProvider timeProvider) : IDisseminationTopic
{
    public string Name => DisseminationTopicNames.DeploymentLoad;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.ActiveMembers;

    public DisseminationTopicOptions Options => options.CurrentValue.Dissemination;

    public bool IsEnabled => Options.Enabled;

    public DisseminationValue CreateItem(SiloAddress origin, SiloRuntimeStatistics statistics)
    {
        var payload = serializer.SerializeToArray(statistics);
        return new DisseminationValue
        {
            Digest = new DisseminationTopicDigest(origin.ToParsableString(), statistics.DateTime.Ticks),
            Root = origin,
            ExpiresAt = timeProvider.GetUtcNow() + Options.StaleItemTtl,
            Payload = payload,
        };
    }

    public IReadOnlyList<DisseminationTopicDigest> GetDigests()
    {
        var digests = new List<DisseminationTopicDigest>();
        foreach (var siloAddress in deploymentLoadPublisher.GetActiveSilosForDissemination())
        {
            var version = deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
                && !deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, statistics.DateTime.Ticks)
                    ? statistics.DateTime.Ticks
                    : long.MinValue;
            digests.Add(new DisseminationTopicDigest(
                siloAddress.ToParsableString(),
                version));
        }

        digests.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return digests;
    }

    public int CompareVersion(DisseminationTopicDigest left, DisseminationTopicDigest right) => left.Version.CompareTo(right.Version);

    public bool IsObsolete(DisseminationTopicDigest digest) =>
        !TryGetSiloAddress(digest.Key, out var siloAddress)
        || deploymentLoadPublisher.IsRuntimeStatisticsObsolete(siloAddress, digest.Version);

    public ValueTask<DisseminationValue?> GetValue(
        DisseminationTopicDigest digest,
        DisseminationTopicDigest? peerDigest,
        CancellationToken cancellationToken)
    {
        if (!TryGetSiloAddress(digest.Key, out var siloAddress)
            || !deploymentLoadPublisher.PeriodicStatistics.TryGetValue(siloAddress, out var statistics)
            || statistics.DateTime.Ticks < digest.Version)
        {
            return ValueTask.FromResult<DisseminationValue?>(null);
        }

        return ValueTask.FromResult<DisseminationValue?>(CreateItem(siloAddress, statistics));
    }

    public ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken)
    {
        if (!TryGetSiloAddress(value.Digest.Key, out var siloAddress))
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var statistics = serializer.Deserialize<SiloRuntimeStatistics>(value.Payload);
        return ValueTask.FromResult(deploymentLoadPublisher.ApplyDisseminatedRuntimeStatistics(siloAddress, statistics));
    }

    public async ValueTask OnFallbackRequired(SiloAddress? peer, DisseminationTopicDigest digest, CancellationToken cancellationToken)
    {
        if (Options.FallbackEnabled && TryGetSiloAddress(digest.Key, out var siloAddress))
        {
            await deploymentLoadPublisher.RefreshSiloStatisticsForDissemination(siloAddress);
        }
    }

    private static bool TryGetSiloAddress(string key, out SiloAddress siloAddress)
    {
        try
        {
            siloAddress = SiloAddress.FromParsableString(key);
            return true;
        }
        catch (FormatException)
        {
            siloAddress = default!;
            return false;
        }
        catch (OverflowException)
        {
            siloAddress = default!;
            return false;
        }
    }
}
