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
    /// Gets whether this activation migrates because its silo is shutting down.
    /// </summary>
    /// <remarks>
    /// The silo deactivates its activations with <see cref="DeactivationReasonCode.ShuttingDown"/> when it shuts down.
    /// An activation which is deactivating for any other reason forwards or rejects its requests.
    /// </remarks>
    private bool MigratesOnRetirement()
        => DeactivationReason.ReasonCode is DeactivationReasonCode.ShuttingDown
            && IsUsingGrainDirectory
            && _shared.Runtime.ServiceProvider.GetService<GrainMigratabilityChecker>() is { } checker
            && checker.IsMigratable(GrainId.Type, ImmovableKind.Any);

    /// <summary>
    /// Removes the queued requests which can migrate with this activation.
    /// </summary>
    /// <returns>The requests, or <see langword="null"/> if there are none.</returns>
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
                    message.AddToCacheInvalidationHeader(previousAddress, Address);
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
