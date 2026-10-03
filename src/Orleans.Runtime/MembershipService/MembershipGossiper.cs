using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Dissemination;

namespace Orleans.Runtime.MembershipService;

internal partial class MembershipGossiper(
    IServiceProvider serviceProvider,
    ILocalSiloDetails localSiloDetails,
    ILogger<MembershipGossiper> logger) : IMembershipGossiper
{
    private MembershipSystemTarget? _membershipSystemTarget;

    public async Task GossipToRemoteSilos(
        List<SiloAddress> gossipPartners,
        MembershipTableSnapshot snapshot,
        SiloAddress updatedSilo,
        SiloStatus updatedStatus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gossipPartners.Count == 0) return;

        LogDebugGossipingStatusToPartners(logger, updatedSilo, updatedStatus, gossipPartners.Count);

        // Direct gossip starts first and owns shutdown-critical delivery.
        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        var directGossip = systemTarget.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus, cancellationToken);
        if (snapshot.Entries.TryGetValue(localSiloDetails.SiloAddress, out var localEntry)
            && localEntry.Status is SiloStatus.Joining or SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping)
        {
            await Task.WhenAll(directGossip, TryGossipViaDissemination(snapshot, cancellationToken));
        }
        else
        {
            await directGossip;
        }
    }

    private async Task TryGossipViaDissemination(MembershipTableSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var disseminationNamespace = serviceProvider.GetRequiredService<MembershipDisseminationNamespace>();
            if (disseminationNamespace.Options.Enabled)
            {
                var dissemination = serviceProvider.GetRequiredService<IDisseminationService>();
                await disseminationNamespace.PublishAsync(dissemination, snapshot, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
