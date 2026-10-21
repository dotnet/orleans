using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Messaging;

/// <summary>
/// The grain extension through which other grains exchange durable messages with this grain.
/// </summary>
[Alias("IMessagingExtension")]
public interface IMessagingExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a message to this grain's inbox.
    /// </summary>
    /// <param name="message">The received command with a required identity and payload; its receiver is this grain.</param>
    /// <param name="cancellationToken">Cancels the caller's wait for delivery.</param>
    /// <remarks>
    /// Direct admission shares the payload array, header dictionary, and header value arrays under the immutable-publication contract.
    /// RPC copying and deserialization isolate payload arrays, header dictionaries, and header value arrays using ordinary Orleans serialization.
    /// A successful result means the inbox durably accepted the message, or already processed it.
    /// Callers retrying a delivery reuse the message's <see cref="InboxMessage.SentAt"/>.
    /// <para>
    /// Deliveries interleave with the grain's other requests. A delivery runs no handler: it validates, deduplicates,
    /// and stages the message's acceptance synchronously, then waits for that journal write, and the messaging pump runs
    /// handlers later. An inbox message sink runs synchronously within the delivery, so it can run while a request is
    /// suspended at an await, as handlers can. Interleaving lets concurrent deliveries share journal writes, and lets a
    /// message reach a grain whose running request waits for it.
    /// </para>
    /// </remarks>
    /// <returns>Result indicating delivery/processing status.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="message"/> has an unset message ID, exceeds identity admission limits, or has invalid payload or headers.
    /// Optional standard metadata is validated where its contract is consumed.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The message's <see cref="InboxMessage.SentAt"/> is later than the receiver's clock plus <c>DeduplicationWindow</c>,
    /// and the message is untracked and above the admission floor, or that send time would extend its record. The inbox
    /// stages nothing for it.
    /// </exception>
    [Alias("DeliverAsync")]
    [AlwaysInterleave]
    ValueTask<DeliveryResult> DeliverAsync(InboxMessage message, CancellationToken cancellationToken = default);
}
