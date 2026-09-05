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

        // Direct gossip owns the shutdown-critical delivery path and starts before optional dissemination work.
        var systemTarget = _membershipSystemTarget ??= serviceProvider.GetRequiredService<MembershipSystemTarget>();
        var directGossipTask = systemTarget.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus, cancellationToken);
        var directGossip = directGossipTask.WaitAsync(cancellationToken);
        try
        {
            if (!IsLocalSiloEligibleForDissemination(snapshot))
            {
                await directGossip;
                return;
            }

            var dissemination = TryGossipViaDissemination(snapshot, cancellationToken);
            await Task.WhenAll(directGossip, dissemination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateFailure(directGossipTask, "Direct membership gossip");
            throw;
        }
    }

    private bool IsLocalSiloEligibleForDissemination(MembershipTableSnapshot snapshot) =>
        snapshot.Entries.TryGetValue(localSiloDetails.SiloAddress, out var localEntry)
        && localEntry.Status is SiloStatus.Joining or SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping;

    private async Task TryGossipViaDissemination(MembershipTableSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var dissemination = serviceProvider.GetService<IDisseminationService>();
            var disseminationNamespace = serviceProvider.GetService<MembershipDisseminationNamespace>();
            if (dissemination is null || disseminationNamespace is null || !disseminationNamespace.Options.Enabled)
            {
                return;
            }

            var publishTask = disseminationNamespace.PublishAsync(dissemination, snapshot, cancellationToken).AsTask();
            try
            {
                await publishTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveLateFailure(publishTask, "Membership dissemination publication");
                throw;
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

    internal void ObserveLateFailure(Task task, string operation)
    {
        // Terminal fault observation remains active after caller cancellation.
        task.ContinueWith(
            completed => LogDebugLateMembershipGossipFailure(logger, completed.Exception!.InnerException!, operation),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Ignore();
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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{Operation} faulted after the caller stopped waiting.")]
    private static partial void LogDebugLateMembershipGossipFailure(
        ILogger logger,
        Exception exception,
        string operation);
}
