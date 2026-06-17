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
    private static readonly HashSet<string> SupportedPayloadKinds = new(StringComparer.Ordinal)
    {
        DisseminationTopicNames.SiloRuntimeStatistics,
    };

    public string Name => DisseminationTopicNames.DeploymentLoad;

    public int ProtocolVersion => 2;

    public DisseminationTopicOptions Options => options.CurrentValue.Dissemination;

    public IReadOnlySet<string> PayloadKinds => SupportedPayloadKinds;

    public bool IsEnabled => Options.Enabled;

    public DisseminationItem CreateItem(SiloAddress origin, SiloRuntimeStatistics statistics)
    {
        var payload = serializer.SerializeToArray(statistics);
        return new DisseminationItem
        {
            Id = new DisseminationItemId(Name, DisseminationValueKey.FromSiloAddress(origin), statistics.DateTime.Ticks, DisseminationTopicNames.SiloRuntimeStatistics),
            Root = origin,
            ExpiresAt = timeProvider.GetUtcNow() + Options.StaleItemTtl,
            Payload = payload,
        };
    }

    public IReadOnlyList<DisseminationItemId> GetDigests()
    {
        var digests = new List<DisseminationItemId>();
        foreach (var entry in deploymentLoadPublisher.PeriodicStatistics)
        {
            if (!deploymentLoadPublisher.IsRuntimeStatisticsObsolete(entry.Key, entry.Value.DateTime.Ticks))
            {
                digests.Add(new DisseminationItemId(
                    Name,
                    DisseminationValueKey.FromSiloAddress(entry.Key),
                    entry.Value.DateTime.Ticks,
                    DisseminationTopicNames.SiloRuntimeStatistics));
            }
        }

        digests.Sort(static (left, right) => string.CompareOrdinal(left.Key.ToString(), right.Key.ToString()));
        return digests;
    }

    public int CompareVersion(DisseminationItemId left, DisseminationItemId right) => left.Version.CompareTo(right.Version);

    public bool IsObsolete(DisseminationItemId id) =>
        !string.Equals(id.PayloadKind, DisseminationTopicNames.SiloRuntimeStatistics, StringComparison.Ordinal)
        || id.Key.SiloAddress is null
        || deploymentLoadPublisher.IsRuntimeStatisticsObsolete(id.Key.SiloAddress, id.Version);

    public ValueTask<DisseminationItem?> GetItem(DisseminationItemId id, CancellationToken cancellationToken)
    {
        if (!string.Equals(id.PayloadKind, DisseminationTopicNames.SiloRuntimeStatistics, StringComparison.Ordinal)
            || id.Key.SiloAddress is null
            || !deploymentLoadPublisher.PeriodicStatistics.TryGetValue(id.Key.SiloAddress, out var statistics)
            || statistics.DateTime.Ticks < id.Version)
        {
            return ValueTask.FromResult<DisseminationItem?>(null);
        }

        return ValueTask.FromResult<DisseminationItem?>(CreateItem(id.Key.SiloAddress, statistics));
    }

    public ValueTask<DisseminationApplyResult> ApplyItem(DisseminationItem item, CancellationToken cancellationToken)
    {
        if (!string.Equals(item.Id.PayloadKind, DisseminationTopicNames.SiloRuntimeStatistics, StringComparison.Ordinal)
            || item.Id.Key.SiloAddress is null)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var statistics = serializer.Deserialize<SiloRuntimeStatistics>(item.Payload);
        return ValueTask.FromResult(deploymentLoadPublisher.ApplyDisseminatedRuntimeStatistics(item.Id.Key.SiloAddress, statistics));
    }

    public async ValueTask OnFallbackRequired(SiloAddress peer, DisseminationItemId id, CancellationToken cancellationToken)
    {
        if (Options.FallbackEnabled && id.Key.SiloAddress is not null)
        {
            await deploymentLoadPublisher.RefreshSiloStatisticsForDissemination(id.Key.SiloAddress);
        }
    }
}
