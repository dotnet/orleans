namespace Orleans.Messaging;

/// <summary>
/// Provides outgoing messages whose payload and receiver the application stores in its own journaled state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IOutbox.Send(MessageId)"/> stages a reference entry which carries the message ID and delivery
/// state, but no payload or receiver, so the payload is journaled once, in the application's state. The outbox keeps
/// every delivery semantic of an ordinary message: delivery after the sending write is durable, retries with backoff,
/// dead-lettering after <c>MaxDeliveryAttempts</c> or <c>MaxOutboxRetryAge</c>, wake-ups, and metrics.
/// </para>
/// <para>
/// The outbox calls <see cref="TryGetMessage"/> when the message is sent and before every delivery attempt. The first
/// registered source which returns the message owns it for the activation's lifetime. When the entry retires, the
/// outbox calls the owner's <see cref="OnRetired"/> exactly once, synchronously, while it stages the entry's removal, so
/// changes the source stages in its journaled state commit in the same journal write as that removal.
/// </para>
/// <para>
/// A source withdraws a pending message by returning <see langword="false"/> from <see cref="TryGetMessage"/>, for
/// example after the application times it out or an administrator removes it. Before its next delivery attempt, the
/// outbox removes the withdrawn entry without delivering it or calling <see cref="OnRetired"/>. An attempt already in
/// flight still completes, so a source can receive <see cref="OnRetired"/> for a message it withdrew.
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
    /// The outbox validates the message. During <see cref="IOutbox.Send(MessageId)"/>, an invalid message fails the
    /// send. Afterwards, an exception or an invalid message breaks the source's contract and fails the activation's
    /// messaging, which deactivates the grain.
    /// </remarks>
    bool TryGetMessage(MessageId messageId, out OutboxMessage message);

    /// <summary>Notifies the source that the outbox retired a message, so the source can release its payload.</summary>
    /// <param name="messageId">The ID of the retired reference entry.</param>
    /// <param name="retirement">Whether the receiver retired the message or the message was dead-lettered.</param>
    /// <remarks>
    /// Stage the release synchronously in journaled state; it commits in the journal write which removes the entry.
    /// A dead-lettered message is materialized into the outbox dead letter before this call.
    /// An exception fails the activation's messaging, which deactivates the grain.
    /// </remarks>
    void OnRetired(MessageId messageId, OutboxMessageRetirement retirement);
}

/// <summary>Describes how an outbox message retired.</summary>
public enum OutboxMessageRetirement
{
    /// <summary>The receiver durably accepted the message, or reported it as a duplicate.</summary>
    Delivered = 1,

    /// <summary>
    /// The receiver dead-lettered the message, or the outbox dead-lettered it after <c>MaxDeliveryAttempts</c> or once
    /// it was older than <c>MaxOutboxRetryAge</c>.
    /// </summary>
    DeadLettered = 2
}
