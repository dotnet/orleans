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
/// journal, the first source which doesn't return <see langword="false"/>, including one which throws, owns it. When the
/// entry retires, the outbox calls the owner's <see cref="OnRetired"/> exactly once, synchronously, while it stages the
/// entry's removal, so changes the source stages in its journaled state commit in the same journal write as that
/// removal.
/// </para>
/// <para>
/// A source withdraws a pending message by returning <see langword="false"/> from <see cref="TryGetMessage"/>, for
/// example after the application times it out or an administrator removes it. Before its next delivery attempt, the
/// outbox removes the withdrawn entry without delivering it or calling <see cref="OnRetired"/>. An attempt already in
/// flight still completes, so a source can receive <see cref="OnRetired"/> for a message it withdrew.
/// </para>
/// <para>
/// A source which can't provide a pending message after its send, for example because its stored payload no longer
/// decodes, throws from <see cref="TryGetMessage"/>; an invalid message is handled the same way. The outbox then
/// dead-letters that entry alone, without delivering it, and calls <see cref="OnRetired"/> with
/// <see cref="OutboxMessageRetirementStatus.DeadLettered"/>; the grain's messaging and its other messages are unaffected. Because no valid message exists, the dead letter's message carries only its
/// <see cref="Envelope.MessageId"/>, with an empty payload and a default <see cref="OutboxMessage.ReceiverId"/>, and its
/// reason records the failure.
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
    /// <see langword="true"/> when this source provides the message; <see langword="false"/> when it doesn't provide the
    /// message or has withdrawn it.
    /// </returns>
    /// <remarks>
    /// The outbox validates the message. During <see cref="IOutbox.Send(MessageId)"/>, an exception or an invalid
    /// message fails the send, and nothing is staged. Afterwards, the entry is durable: an exception or an invalid message,
    /// including one with a different <see cref="Envelope.MessageId"/> or addressed to the sending grain, dead-letters the
    /// entry with its failure as the reason, counting the failed materialization as a delivery attempt, and the outbox
    /// then calls <see cref="OnRetired"/> with <see cref="OutboxMessageRetirementStatus.DeadLettered"/>.
    /// </remarks>
    bool TryGetMessage(MessageId messageId, out OutboxMessage message);

    /// <summary>Notifies the source that the outbox retired a message, so the source can release its payload.</summary>
    /// <param name="messageId">The ID of the retired reference entry.</param>
    /// <param name="retirement">How the message retired, and the dead letter's reason for a dead-lettered message.</param>
    /// <remarks>
    /// Stage the release synchronously in journaled state; it commits in the journal write which removes the entry.
    /// A dead-lettered message is materialized into the outbox dead letter before this call, unless the source failed to
    /// provide it. Don't depend on decoding the payload here: this call also retires a message whose payload the source
    /// can no longer provide. An exception fails the activation's messaging, which deactivates the grain.
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
    /// source failed to provide it.
    /// </summary>
    DeadLettered = 2,

    /// <summary>The receiver dead-lettered the message, with the reason which it reported.</summary>
    Rejected = 3
}
