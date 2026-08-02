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

        if (await TryGossipViaDissemination(snapshot))
        {
            return;
        }

        LogDebugGossipingStatusToPartners(logger, updatedSilo, updatedStatus, gossipPartners.Count);

        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        await systemTarget.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus);
    }

    private async Task<bool> TryGossipViaDissemination(MembershipTableSnapshot snapshot)
    {
        try
        {
            var dissemination = serviceProvider.GetService<DisseminationService>();
            var topic = serviceProvider.GetService<MembershipDisseminationTopic>();
            if (dissemination is null || topic is null || !topic.IsEnabled)
            {
                return false;
            }

            var localSilo = serviceProvider.GetRequiredService<ILocalSiloDetails>().SiloAddress;
            var item = topic.CreateItem(localSilo, snapshot);
            return await dissemination.Publish(topic.Name, item, targetPeers: null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogDebugMembershipDisseminationFailed(logger, exception);
            return false;
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
