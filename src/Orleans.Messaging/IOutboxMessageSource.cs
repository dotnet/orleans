namespace Orleans.Messaging;

/// <summary>
/// Provides outgoing messages whose payload and receiver the application stores in its own journaled state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IOutbox.Send(MessageId)"/> stages a reference entry which carries the message ID and delivery
/// state, but no payload or receiver, so the payload is journaled once, in the application's state. The outbox keeps
/// every delivery semantic of an ordinary message: delivery after the sending write is durable, retries with backoff,
/// dead-lettering after <c>OutboxRetryTimeout</c>, wake-ups, and metrics.
/// </para>
/// <para>
/// The outbox calls <see cref="TryGetMessage"/> when the message is sent and before every delivery attempt. The first
/// registered source which returns the message owns it for the activation's lifetime; for an entry recovered from the
/// journal, the first source which returns the message or throws owns it. When the entry retires, the outbox calls the
/// owner's <see cref="OnRetired"/> once for that entry in the current activation, synchronously, while it stages the
/// entry's removal. The source's journaled changes and the removal commit in the same journal write. If the activation
/// fails before that write commits, recovery restores the pending entry and the source's prior journaled state, and
/// retiring the recovered entry invokes <see cref="OnRetired"/> again.
/// </para>
/// <para>
/// A source withdraws a pending message through <see cref="IOutbox.Withdraw"/>, for example after the application times
/// it out or an administrator removes it, and releases its payload in the same journal write. The outbox removes the
/// entry without calling <see cref="OnRetired"/> and makes no further delivery attempt. An attempt already in flight can
/// still deliver the message and reads the buffers which <see cref="TryGetMessage"/> returned, so keep them unchanged:
/// release them by dropping references rather than reusing them.
/// </para>
/// <para>
/// A source which can't provide a pending message after its send, for example because its stored payload no longer
/// decodes, throws from <see cref="TryGetMessage"/>; an invalid message is handled the same way. The outbox then
/// dead-letters that entry alone, without delivering it, and calls <see cref="OnRetired"/> with
/// <see cref="OutboxMessageRetirementStatus.DeadLettered"/>; the grain's messaging and its other messages are
/// unaffected. A recovered entry which no registered source provides is dead-lettered the same way, without an owner to
/// notify. Because no valid message exists, the dead letter's message carries only its <see cref="Envelope.MessageId"/>,
/// with an empty payload and a default <see cref="OutboxMessage.ReceiverId"/>, and its reason records the failure.
/// </para>
/// <para>
/// All calls run on the activation scheduler. Register sources during activation setup, before journal recovery: a
/// recovered reference entry fails the activation when no source is registered. While a source provides a message, it
/// returns the same message, envelope and receiver alike, for every call.
/// </para>
/// </remarks>
public interface IOutboxMessageSource
{
    /// <summary>Materializes a message for its send or a delivery attempt.</summary>
    /// <param name="messageId">The ID of a reference entry.</param>
    /// <param name="message">
    /// When this method returns <see langword="true"/>, a valid message whose envelope's <see cref="Envelope.MessageId"/>
    /// is <paramref name="messageId"/> and whose receiver is another grain.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when this source provides the message; <see langword="false"/> when the message isn't
    /// this source's.
    /// </returns>
    /// <remarks>
    /// The outbox validates the message. During <see cref="IOutbox.Send(MessageId)"/>, an exception or an invalid
    /// message fails the send, and nothing is staged. Afterwards, the entry is durable: an exception, an invalid message,
    /// including one with a different <see cref="Envelope.MessageId"/> or addressed to the sending grain, or
    /// <see langword="false"/> from the source which owns the entry dead-letters the entry with its failure as the
    /// reason, counting the failed materialization as a delivery attempt, and the outbox then calls
    /// <see cref="OnRetired"/> with <see cref="OutboxMessageRetirementStatus.DeadLettered"/>.
    /// </remarks>
    bool TryGetMessage(MessageId messageId, out OutboxMessage message);

    /// <summary>Notifies the source that the outbox is staging a message's retirement, so the source can release its payload.</summary>
    /// <param name="messageId">The ID of the retired reference entry.</param>
    /// <param name="retirement">How the message retired, and the dead letter's reason for a dead-lettered message.</param>
    /// <remarks>
    /// <para>
    /// Stage the release and other durable effects synchronously in journaled state; they commit atomically with the
    /// entry's removal. This callback runs once for the entry in the current activation. Recovery can invoke it again
    /// when the retirement write has not committed, starting from the source's recovered journaled state.
    /// Make any effects outside that journal write idempotent across repeated calls.
    /// Stage a durable application outcome or follow-up work here for
    /// <see cref="OutboxMessageRetirementStatus.DeadLettered"/> and
    /// <see cref="OutboxMessageRetirementStatus.Rejected"/>, so recovery preserves the application's response even
    /// after diagnostic dead letters are compacted. A dead-lettered send can have been accepted by an earlier attempt;
    /// reconcile that uncertain outcome using application-level idempotency.
    /// </para>
    /// <para>
    /// Before this call, the outbox copies a dead-lettered message's payload and header values into its dead letter,
    /// unless the source failed to provide the message, so the source can release or reuse its buffers here. Don't depend
    /// on decoding the payload here: this call also retires a message whose payload the source can no longer provide. An
    /// exception fails the activation's messaging, which deactivates the grain.
    /// </para>
    /// </remarks>
    void OnRetired(MessageId messageId, OutboxMessageRetirement retirement);
}

/// <summary>Describes how an outbox message retired.</summary>
/// <remarks>The outbox creates retirements; sources read them in <see cref="IOutboxMessageSource.OnRetired"/>.</remarks>
public readonly struct OutboxMessageRetirement
{
    /// <summary>Gets how the message retired.</summary>
    public OutboxMessageRetirementStatus Status { get; init; }

    /// <summary>
    /// Gets the dead letter's reason for a message which the receiver rejected or the outbox dead-lettered, such as the
    /// rejection which the receiver reported; <see langword="null"/> for a delivered message.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the application-defined code which the receiver's inbox message sink reported with a rejection, as
    /// <see cref="DeliveryResult.Code"/>; <see langword="null"/> when the receiver reported none or the message didn't
    /// retire as <see cref="OutboxMessageRetirementStatus.Rejected"/>.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>Creates a retirement of a message which the receiver durably accepted or reported as a duplicate.</summary>
    public static OutboxMessageRetirement Delivered() => new() { Status = OutboxMessageRetirementStatus.Delivered };

    /// <summary>Creates a retirement of a message which the outbox dead-lettered for <paramref name="reason"/>.</summary>
    /// <param name="reason">The dead letter's reason.</param>
    public static OutboxMessageRetirement DeadLettered(string reason) => new()
    {
        Status = OutboxMessageRetirementStatus.DeadLettered,
        Reason = reason
    };

    /// <summary>Creates a retirement of a message which the receiver dead-lettered for <paramref name="reason"/>.</summary>
    /// <param name="reason">The reason which the receiver reported.</param>
    /// <param name="code">The application-defined code which the receiver reported, if any.</param>
    public static OutboxMessageRetirement Rejected(string reason, string? code = null) => new()
    {
        Status = OutboxMessageRetirementStatus.Rejected,
        Reason = reason,
        Code = code
    };
}

/// <summary>Describes how an outbox message retired.</summary>
public enum OutboxMessageRetirementStatus
{
    /// <summary>The receiver durably accepted the message, or reported it as a duplicate.</summary>
    Delivered = 1,

    /// <summary>
    /// The outbox dead-lettered the message: its delivery attempts didn't succeed before its retry limits elapsed, or its
    /// source failed to provide it, or the receiver answered <see cref="DeliveryStatus.Expired"/>. The receiver's
    /// earlier delivery outcome is unknown: an earlier attempt can have been durably accepted.
    /// </summary>
    DeadLettered = 2,

    /// <summary>
    /// The receiver durably rejected the message, with the reason and code which it reported.
    /// </summary>
    Rejected = 3
}
