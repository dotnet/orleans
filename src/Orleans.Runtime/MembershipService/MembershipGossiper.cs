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

        LogDebugGossipingStatusToPartners(logger, updatedSilo, updatedStatus, gossipPartners.Count);

        await TryGossipViaDissemination(snapshot);

        // Direct gossip preserves prompt delivery to every cluster member during rolling upgrades.
        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        await systemTarget.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus);
    }

    private async Task TryGossipViaDissemination(MembershipTableSnapshot snapshot)
    {
        try
        {
            var dissemination = serviceProvider.GetService<IDisseminationService>();
            var disseminationNamespace = serviceProvider.GetService<MembershipDisseminationNamespace>();
            if (dissemination is null || disseminationNamespace is null || !disseminationNamespace.Options.Enabled)
            {
                return;
            }

            await disseminationNamespace.PublishAsync(dissemination, snapshot, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogDebugMembershipDisseminationFailed(logger, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Gossiping {Silo} status {Status} to {NumPartners} partners"
    )]
    private static partial void LogDebugGossipingStatusToPartners(ILogger logger, SiloAddress silo, SiloStatus status, int numPartners);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Membership dissemination failed. Direct membership gossip continues delivery.")]
    private static partial void LogDebugMembershipDisseminationFailed(ILogger logger, Exception exception);
}
