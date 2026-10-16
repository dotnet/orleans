using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Runtime.Placement;

namespace Orleans.Runtime;

/// <summary>
/// Migration of the requests which are queued for this activation, together with the activation.
/// </summary>
internal sealed partial class ActivationData
{
    /// <summary>
    /// Gets whether this activation migrates because its silo is retiring.
    /// </summary>
    /// <remarks>
    /// This includes an activation whose deactivation began before retirement for a routine reason. An activation which is deactivating
    /// because it failed, is a duplicate, is unresponsive, or has suspect state or registration does not migrate, so its requests are
    /// forwarded or rejected as before.
    /// </remarks>
    private bool MigratesOnRetirement()
        => _shared.InternalRuntime.Catalog.IsRetiring
            && DeactivationReason.ReasonCode is DeactivationReasonCode.ShuttingDown
                or DeactivationReasonCode.ActivationIdle
                or DeactivationReasonCode.ApplicationRequested
                or DeactivationReasonCode.RuntimeRequested
                or DeactivationReasonCode.HighMemoryPressure
            && IsUsingGrainDirectory
            && _shared.Runtime.ServiceProvider.GetService<GrainMigratabilityChecker>() is { } checker
            && checker.IsMigratable(GrainId.Type, ImmovableKind.Any);

    /// <summary>
    /// Removes the queued requests which can migrate with this activation.
    /// </summary>
    /// <returns>The requests, or <see langword="null"/> if there are none.</returns>
    /// <remarks>
    /// While the requests are in transit, a cancellation for one of them is kept until the migration's outcome is known, since the new activation may not exist yet.
    /// <see cref="EndRequestMigration"/> ends the transit.
    /// </remarks>
    private List<Message>? TakeRequestsForMigration()
    {
        var maxForwardCount = _shared.MessagingOptions.MaxForwardCount;
        List<Message>? requests = null;
        lock (_lock)
        {
            _waitingRequests.RemoveAll(entry =>
            {
                // Local-only messages are not allowed to escape the activation.
                // Migration counts as a forward, so requests which have exhausted their forwards remain queued and are rejected.
                if (entry.Message.IsLocalOnly || entry.Message.ForwardCount >= maxForwardCount)
                {
                    return false;
                }

                (requests ??= []).Add(entry.Message);
                return true;
            });

            if (requests is not null)
            {
                _extras ??= new();
                _extras.IsMigratingRequests = true;
            }
        }

        return requests;
    }

    /// <summary>
    /// Disposes of the requests which were taken for migration, according to the migration's outcome.
    /// </summary>
    /// <remarks>
    /// Cancellations which arrived while the requests were in transit were sent to the target as they arrived, which could precede the migration.
    /// They are applied here if the requests stay, or sent again if the target may own the requests.
    /// </remarks>
    private void EndRequestMigration(List<Message>? requests, MigratedRequestsOutcome outcome)
    {
        if (requests is null)
        {
            return;
        }

        HashSet<(GrainId SendingGrain, CorrelationId MessageId)>? cancellations;
        if (outcome is not MigratedRequestsOutcome.Retained)
        {
            SiloAddress? target;
            lock (_lock)
            {
                cancellations = EndInTransitCancellations();
                target = ForwardingAddress;
            }

            // The target owns the requests, or may own them. Requests which this silo received still hold their serialized bodies, which the migration copied.
            // Requests from callers on this silo share their body with the caller's pending call, so they are left intact.
            foreach (var request in requests)
            {
                request.ReleaseBodyBuffer();
            }

            if (cancellations is not null)
            {
                var cancellationManager = _shared.InternalRuntime.RuntimeClient.CancellationManager;
                foreach (var (sendingGrain, messageId) in cancellations)
                {
                    cancellationManager.SignalCancellation(target, GrainId, sendingGrain, messageId);
                }
            }

            return;
        }

        // The requests return to the front of the queue, except those canceled while in transit.
        List<Message>? canceled = null;
        lock (_lock)
        {
            cancellations = EndInTransitCancellations();
            var queuedTime = CoarseStopwatch.StartNew();
            var retained = new List<(Message, CoarseStopwatch)>(requests.Count);
            foreach (var request in requests)
            {
                if (cancellations?.Contains((request.SendingGrain, request.Id)) == true)
                {
                    (canceled ??= []).Add(request);
                }
                else
                {
                    retained.Add((request, queuedTime));
                }
            }

            _waitingRequests.InsertRange(0, retained);
        }

        if (canceled is not null)
        {
            foreach (var request in canceled)
            {
                CompleteCanceledRequest(request);
            }
        }
    }

    /// <summary>
    /// Queues the requests which were queued for this grain's previous activation when it migrated here, ahead of any other request.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this activation took ownership of the requests; <see langword="false"/> if it has begun deactivating
    /// or would reject some of the requests as overloaded, in which case none of the requests were queued.
    /// </returns>
    /// <remarks>
    /// The catalog calls this before the activation begins activating, so no request runs before these.
    /// </remarks>
    internal bool TryEnqueueMigratedRequests(List<Message> messages)
    {
        Debug.Assert(!IsStatelessWorker, "Stateless workers do not migrate.");
        lock (_lock)
        {
            // The catalog publishes the activation before this, so retirement can begin deactivating it first. Deactivation decides the disposition of queued requests.
            if (State is not ActivationState.Creating)
            {
                return false;
            }

            // ReceiveRequest rejects a request when the activation already holds more than the hard limit, so decline a batch whose last request it would reject.
            var hardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit;
            if (hardLimit > 0 && _runningRequests.Count + _waitingRequests.Count + messages.Count - 1 > hardLimit)
            {
                return false;
            }

            var queuedTime = CoarseStopwatch.StartNew();
            var previousAddress = PreviousRegistration;
            var migrated = new List<(Message, CoarseStopwatch)>(messages.Count);
            foreach (var message in messages)
            {
                if (previousAddress is not null)
                {
                    message.AddToCacheInvalidationHeader(previousAddress, Address);
                }

                migrated.Add((message, queuedTime));
            }

            _waitingRequests.InsertRange(0, migrated);
            return true;
        }
    }
}
