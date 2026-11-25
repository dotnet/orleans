using System.Collections.Generic;
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
        List<Message>? expired = null;
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

                // Expired requests are dropped, as they are when received. Their callers have already timed out.
                if (entry.Message.IsExpired)
                {
                    (expired ??= []).Add(entry.Message);
                }
                else
                {
                    (requests ??= []).Add(entry.Message);
                }

                return true;
            });
        }

        if (expired is not null)
        {
            foreach (var message in expired)
            {
                _shared.InternalRuntime.MessagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Dispatch);
                message.Dispose();
            }
        }

        return requests;
    }
}
