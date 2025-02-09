#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService;

internal class MembershipGossiper(IServiceProvider serviceProvider, ILogger<MembershipGossiper> logger) : IMembershipGossiper
{
    private MembershipSystemTarget? _membershipSystemTarget;

    public Task GossipToRemoteSilos(
        List<SiloAddress> gossipPartners,
        MembershipTableSnapshot snapshot,
        SiloAddress updatedSilo,
        SiloStatus updatedStatus)
    {
        if (gossipPartners.Count == 0) return Task.CompletedTask;

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Gossiping {Silo} status {Status} to {NumPartners} partners",
                updatedSilo,
                updatedStatus,
                gossipPartners.Count);
        }

        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        return systemTarget.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus);
    }
}
