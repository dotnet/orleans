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
        if (gossipPartners.Count == 0) return;

        LogDebugGossipingStatusToPartners(logger, updatedSilo, updatedStatus, gossipPartners.Count);

        // Direct gossip owns the shutdown-critical delivery path and starts before optional dissemination work.
        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        var directGossip = systemTarget.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus)
            .WaitAsync(cancellationToken);
        if (!IsLocalSiloEligibleForDissemination(snapshot))
        {
            await directGossip;
            return;
        }

        var dissemination = TryGossipViaDissemination(snapshot, cancellationToken);
        await Task.WhenAll(directGossip, dissemination);
    }

    private bool IsLocalSiloEligibleForDissemination(MembershipTableSnapshot snapshot) =>
        snapshot.Entries.TryGetValue(localSiloDetails.SiloAddress, out var localEntry)
        && localEntry.Status is SiloStatus.Joining or SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping;

    private async Task TryGossipViaDissemination(MembershipTableSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var dissemination = serviceProvider.GetService<IDisseminationService>();
            var disseminationNamespace = serviceProvider.GetService<MembershipDisseminationNamespace>();
            if (dissemination is null || disseminationNamespace is null || !disseminationNamespace.Options.Enabled)
            {
                return;
            }

            await disseminationNamespace.PublishAsync(dissemination, snapshot, cancellationToken);
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
