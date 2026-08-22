using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Dissemination;

namespace Orleans.Runtime.MembershipService;

internal partial class MembershipGossiper(IServiceProvider serviceProvider, ILogger<MembershipGossiper> logger) : IMembershipGossiper
{
    private MembershipSystemTarget? _membershipSystemTarget;

    public async Task GossipToRemoteSilos(
        List<SiloAddress> gossipPartners,
        MembershipTableSnapshot snapshot,
        SiloAddress updatedSilo,
        SiloStatus updatedStatus)
    {
        if (gossipPartners.Count == 0) return;

        var fallbackPartners = await TryGossipViaDissemination(snapshot, gossipPartners);
        if (fallbackPartners.Count == 0)
        {
            return;
        }

        LogDebugGossipingStatusToPartners(logger, updatedSilo, updatedStatus, fallbackPartners.Count);

        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        await systemTarget.GossipToRemoteSilos(fallbackPartners, snapshot, updatedSilo, updatedStatus);
    }

    private async Task<List<SiloAddress>> TryGossipViaDissemination(
        MembershipTableSnapshot snapshot,
        List<SiloAddress> gossipPartners)
    {
        try
        {
            var dissemination = serviceProvider.GetService<DisseminationService>();
            var topic = serviceProvider.GetService<MembershipDisseminationTopic>();
            if (dissemination is null || topic is null || !topic.IsEnabled)
            {
                return gossipPartners;
            }

            var localSilo = serviceProvider.GetRequiredService<ILocalSiloDetails>().SiloAddress;
            var item = topic.CreateItem(localSilo, snapshot);
            if (!await dissemination.Publish(topic.Name, item, targetPeers: null, CancellationToken.None))
            {
                return gossipPartners;
            }

            return [.. dissemination.GetUnconfirmedPeers(topic.Name, topic.MembershipScope, gossipPartners)];
        }
        catch (Exception exception)
        {
            LogDebugMembershipDisseminationFailed(logger, exception);
            return gossipPartners;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Gossiping {Silo} status {Status} to {NumPartners} partners"
    )]
    private static partial void LogDebugGossipingStatusToPartners(ILogger logger, SiloAddress silo, SiloStatus status, int numPartners);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Membership dissemination failed. Falling back to legacy membership gossip.")]
    private static partial void LogDebugMembershipDisseminationFailed(ILogger logger, Exception exception);
}
