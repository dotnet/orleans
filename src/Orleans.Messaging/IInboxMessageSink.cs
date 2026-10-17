namespace Orleans.Messaging;

/// <summary>
/// Accepts delivered messages directly into application-owned journaled state, so their payload is journaled once.
/// </summary>
/// <remarks>
/// <para>
/// After validation and deduplication, the inbox offers each message to the registered sinks in registration order,
/// including messages the grain sends to itself and messages with <see cref="Envelope.DeliverAt"/>. The first sink
/// which takes the message decides its <see cref="DeliveryResult"/>. A sink which reports
/// <see cref="DeliveryStatus.Accepted"/> has synchronously staged the message's effects in its own journaled state. The
/// inbox then journals only the message's processed record, without its payload, in the same journal write, and reports
/// the result once that write is durable. Run effects which depend on the write, such as starting work, from
/// <see cref="Orleans.Journaling.IStateMachine.OnWriteCompleted"/>.
/// </para>
/// <para>
/// A sink-owned message never occupies inbox capacity and has no inbox retry lifecycle: the sink owns its processing,
/// including honoring the message's <see cref="Envelope.DeliverAt"/>. Messages which every sink declines are accepted by
/// the inbox and dispatched to <see cref="IInboxHandler"/> instances once their <see cref="Envelope.DeliverAt"/> has
/// passed; decline a scheduled message to have the inbox hold it until then.
/// </para>
/// <para>
/// Sinks must be idempotent. The inbox deduplicates the message ID while its processed record is retained for
/// <c>DeduplicationWindow</c>; after that, the message is offered again, and a sink which still holds it reports
/// <see cref="DeliveryStatus.Duplicate"/>. A message whose journal write fails is redelivered to the next activation,
/// which recovers the sink's state from the journal.
/// </para>
/// </remarks>
public interface IInboxMessageSink
{
    /// <summary>Takes a message into the sink's journaled state, or declines it.</summary>
    /// <param name="message">The validated, deduplicated message.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the delivery result which the inbox reports to the sender.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the sink takes the message and reports <paramref name="result"/>;
    /// <see langword="false"/> when it declines the message without staging anything.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Runs synchronously on the activation scheduler. Validate the message before staging any change. The sink reports
    /// one of these statuses:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="DeliveryStatus.Accepted"/>: the sink staged the message's effects, and the inbox records the message
    /// as processed in the same write.
    /// </description></item>
    /// <item><description>
    /// <see cref="DeliveryStatus.Duplicate"/>: the sink already holds the message and stages nothing. The inbox reports
    /// it once any write in progress, which can hold the original, is durable.
    /// </description></item>
    /// <item><description>
    /// <see cref="DeliveryStatus.Backpressured"/>: the sink is at capacity and stages nothing, so the sender retries
    /// later. A message the grain sends to itself can't wait for a retry, so this status fails that send.
    /// </description></item>
    /// <item><description>
    /// <see cref="DeliveryStatus.DeadLettered"/>: the sink rejects the message and stages nothing. The inbox
    /// dead-letters it with its payload and the result's reason, and records it as processed.
    /// </description></item>
    /// </list>
    /// <para>
    /// A sink which throws or reports another status must leave no staged change, and the inbox dead-letters the message
    /// with the failure as its reason.
    /// </para>
    /// </remarks>
    bool TryAccept(InboxMessage message, out DeliveryResult result);
}
