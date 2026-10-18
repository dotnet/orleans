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
    /// While the requests are in transit, a cancellation for one of them is sent to the grain as a request, which travels with this activation's other requests.
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
        }

        return requests;
    }

    /// <summary>
    /// Disposes of the requests which were taken for migration, according to the migration's outcome.
    /// </summary>
    private void EndRequestMigration(List<Message>? requests, MigratedRequestsOutcome outcome)
    {
        if (requests is null)
        {
            return;
        }

        if (outcome is MigratedRequestsOutcome.Retained)
        {
            // The requests return to the front of the queue, from which deactivation forwards them.
            lock (_lock)
            {
                var queuedTime = CoarseStopwatch.StartNew();
                var retained = new List<(Message, CoarseStopwatch)>(requests.Count);
                foreach (var request in requests)
                {
                    retained.Add((request, queuedTime));
                }

                _waitingRequests.InsertRange(0, retained);
            }

            return;
        }

        // The target owns the requests, or may own them. Requests which this silo received still hold their serialized bodies, which the migration copied.
        // Requests from callers on this silo share their body with the caller's pending call, so they are left intact.
        foreach (var request in requests)
        {
            request.ReleaseBodyBuffer();
        }
    }

    /// <summary>
    /// Adds an update to <paramref name="message"/> which replaces the grain's previous address with this activation's address in callers' caches.
    /// </summary>
    /// <remarks>
    /// A request which an earlier forward or migration moved already carries an update for this grain, from its original address to the address it moved to.
    /// That update is composed with this one, so that callers which cached the original address cache this activation's address.
    /// </remarks>
    private void AddMigratedCacheUpdate(Message message, GrainAddress previousAddress)
    {
        if (message.CacheInvalidationHeader is { } updates)
        {
            for (var i = 0; i < updates.Count; i++)
            {
                if (updates[i].GrainId == GrainId)
                {
                    updates[i] = new GrainAddressCacheUpdate(updates[i].InvalidGrainAddress, Address);
                    return;
                }
            }
        }

        message.AddToCacheInvalidationHeader(previousAddress, Address);
    }

    /// <summary>
    /// Queues the requests which were queued for this grain's previous activation when it migrated here, ahead of any other request.
    /// </summary>
    /// <param name="messages">The requests.</param>
    /// <param name="previousAddress">The previous activation's address, which the requests' callers replace with this activation's address in their caches.</param>
    /// <returns>
    /// <see langword="true"/> if this activation took ownership of the requests; <see langword="false"/> if it has begun deactivating
    /// or would reject some of the requests as overloaded, in which case none of the requests were queued.
    /// </returns>
    /// <remarks>
    /// The catalog calls this before the activation begins activating, so no request runs before these.
    /// Callers of accepted one-way requests receive a cache-invalidation-only response with this activation's address.
    /// </remarks>
    internal bool TryEnqueueMigratedRequests(List<Message> messages, GrainAddress? previousAddress)
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
            var migrated = new List<(Message, CoarseStopwatch)>(messages.Count);
            foreach (var message in messages)
            {
                if (previousAddress is not null)
                {
                    AddMigratedCacheUpdate(message, previousAddress);
                }

                migrated.Add((message, queuedTime));
            }

            _waitingRequests.InsertRange(0, migrated);
        }

        if (previousAddress is not null)
        {
            var messageCenter = _shared.InternalRuntime.RuntimeClient.MessageCenter;
            foreach (var message in messages)
            {
                if (message.Direction is Message.Directions.OneWay)
                {
                    messageCenter.SendRejectionResponse(
                        message,
                        Message.RejectionTypes.CacheInvalidation,
                        "One-way request migrated to a new activation",
                        exception: null);
                }
            }
        }

        return true;
    }
}
