namespace Orleans.Messaging;

/// <summary>
/// Provides the envelopes of outgoing messages whose payload the application stores in its own journaled state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IOutbox.SendFromSource"/> stages a reference entry which carries the message ID, receiver, and delivery
/// state, but no payload, so the payload is journaled once, in the application's state. The outbox keeps every
/// delivery semantic of an ordinary message: delivery after the sending write is durable, retries with backoff,
/// dead-lettering after <c>MaxDeliveryAttempts</c> or <c>MaxOutboxRetryAge</c>, wake-ups, and metrics.
/// </para>
/// <para>
/// The outbox resolves each reference entry to the first registered source whose <see cref="CanProvide"/> returns
/// <see langword="true"/>, when the message is sent and when the outbox recovers it, and calls
/// <see cref="GetEnvelope"/> on every delivery attempt. When the entry retires, the outbox calls
/// <see cref="OnRetired"/> exactly once, synchronously, while it stages the entry's removal, so changes the source
/// stages in its journaled state commit in the same journal write as that removal. Register sources during activation
/// setup, before journal recovery: a recovered reference entry which no source can provide fails the activation.
/// </para>
/// <para>
/// All calls run on the activation scheduler. The source keeps the message available from the send until
/// <see cref="OnRetired"/>, and returns the same command for every call.
/// </para>
/// </remarks>
public interface IOutboxMessageSource
{
    /// <summary>Determines whether this source provides the message.</summary>
    /// <param name="messageId">The ID of a reference entry.</param>
    /// <returns><see langword="true"/> when this source provides the message.</returns>
    bool CanProvide(HierarchicalKey messageId);

    /// <summary>Materializes the envelope of a message for a delivery attempt or a dead letter.</summary>
    /// <param name="messageId">The ID of a reference entry which this source provides.</param>
    /// <returns>A valid envelope whose <see cref="Envelope.MessageId"/> is <paramref name="messageId"/>.</returns>
    /// <remarks>
    /// The outbox validates the envelope. An exception or an invalid envelope breaks the source's contract and fails the
    /// activation's messaging, which deactivates the grain.
    /// </remarks>
    Envelope GetEnvelope(HierarchicalKey messageId);

    /// <summary>Notifies the source that the outbox retired a message, so the source can release its payload.</summary>
    /// <param name="messageId">The ID of the retired reference entry.</param>
    /// <param name="retirement">Whether the receiver retired the message or the message was dead-lettered.</param>
    /// <remarks>
    /// Stage the release synchronously in journaled state; it commits in the journal write which removes the entry.
    /// A dead-lettered message's envelope is materialized into the outbox dead letter before this call.
    /// An exception fails the activation's messaging, which deactivates the grain.
    /// </remarks>
    void OnRetired(HierarchicalKey messageId, OutboxMessageRetirement retirement);
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
