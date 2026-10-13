namespace Orleans.Messaging;

/// <summary>
/// Accepts delivered messages directly into application-owned journaled state, so their payload is journaled once.
/// </summary>
/// <remarks>
/// <para>
/// After validation and deduplication, the inbox offers each message without <see cref="Envelope.DeliverAt"/> to the
/// registered sinks in registration order, including messages the grain sends to itself. A sink which returns
/// <see langword="true"/> has synchronously staged the message's effects in its own journaled state. The inbox then
/// journals only the message's processed record, without its payload, in the same journal write, and reports
/// <see cref="DeliveryStatus.Accepted"/> once that write is durable. Run effects which depend on the write, such as
/// starting work, from <c>Orleans.Journaling.IStateMachine.OnWriteCompleted</c>.
/// </para>
/// <para>
/// A sink-accepted message never occupies inbox capacity and has no inbox retry or dead-letter lifecycle: the sink
/// owns its processing. Messages which every sink declines, and messages with <see cref="Envelope.DeliverAt"/>, are
/// accepted by the inbox and dispatched to <see cref="IInboxHandler"/> instances as usual.
/// </para>
/// <para>
/// Sinks must be idempotent. The inbox deduplicates the message ID only while its processed record is retained for
/// <c>DeduplicationWindow</c>; a later redelivery is offered again. A message whose journal write fails is
/// redelivered to the next activation, which recovers the sink's state from the journal.
/// </para>
/// </remarks>
public interface IInboxMessageSink
{
    /// <summary>Accepts a message into the sink's journaled state, or declines it.</summary>
    /// <param name="message">The validated, deduplicated message.</param>
    /// <returns>
    /// <see langword="true"/> when the sink staged the message's effects; <see langword="false"/> when it declines the
    /// message without staging anything.
    /// </returns>
    /// <remarks>
    /// Runs synchronously on the activation scheduler. Validate the message before staging any change. A sink which
    /// throws must leave no staged change: the inbox dead-letters the message with its payload, records its ID as
    /// processed, and reports <see cref="DeliveryStatus.DeadLettered"/>.
    /// </remarks>
    bool TryAccept(InboxMessage message);
}
