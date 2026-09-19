using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams;

/// <summary>
/// Reconciles failed reads for a checkpointing receiver.
/// </summary>
internal interface IQueueAdapterReceiverReadRecovery
{
    /// <summary>
    /// Restores read continuity using the receiver's current ownership, source position, and staged records.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task which completes after all failed-read effects have been reconciled.</returns>
    /// <remarks>
    /// Successful completion certifies that subsequent handoffs account for every record after
    /// the last successful handoff, including internally admitted records whose notifications
    /// were not returned. Recovery preserves the current run's starting position and reports
    /// retention gaps or ownership loss as failures. Cancellation supplies no certificate.
    /// The pulling agent serializes recovery with reads and drains accepted recovery before shutdown.
    /// </remarks>
    Task RecoverReadAsync(CancellationToken cancellationToken);
}
