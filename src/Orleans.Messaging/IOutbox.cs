using System;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Stages opaque durable messages alongside a grain's journaled business state.
/// </summary>
/// <remarks>
/// The outbox state machine's asynchronous write preparation establishes a durable self-wakeup before capture.
/// Its write-completion callback makes captured messages eligible for dispatch.
/// Each wakeup checks the grain's messages and their retry deadlines. Messages remain pending until the destination
/// acknowledges durable acceptance, recognizes a duplicate, or reports a terminal delivery outcome.
/// Applications namespace command identities and define ordering in their protocols.
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Synchronously stages an outgoing command for the grain's next journal write.
    /// </summary>
    /// <param name="message">The fully built outgoing message and required receiver.</param>
    /// <remarks>
    /// Direct sends share the payload array, header dictionary, and header value arrays. Keep these objects unchanged after publication.
    /// Equivalent repeated identities retain the original message and headers; payload, optional subject,
    /// optional <see cref="Envelope.DeliverAt"/>, and destination must match. Custom headers do not change pending intent equality. Conflicts fail explicitly.
    /// Inbox handlers stage outgoing
    /// messages in their synchronous final block before calling <see cref="IInboxHandlerContext.Complete"/>
    /// or <see cref="IInboxHandlerContext.Fail"/>.
    /// Ordinary callers persist staged messages using their journaled state manager.
    /// An explicit write retry retains pending business changes and messages after a scheduling failure.
    /// </remarks>
    /// <exception cref="ArgumentException">The envelope has an unset identity, invalid subject or destination, or exceeds identity/subject limits.</exception>
    /// <exception cref="InvalidOperationException">
    /// The identity conflicts, or the outbox is unavailable.
    /// </exception>
    void Send(OutboxMessage message);

    /// <summary>
    /// Synchronously stages, for the grain's next journal write, a message whose envelope a registered
    /// <see cref="IOutboxMessageSource"/> provides.
    /// </summary>
    /// <param name="messageId">The message ID, which a registered source provides.</param>
    /// <param name="receiverId">The receiving grain, which must not be the sending grain.</param>
    /// <remarks>
    /// The outbox journals the message ID, receiver, and delivery state, but not the payload, and delivers the message
    /// like one staged through <see cref="Send(OutboxMessage)"/>. Stage the payload in the source's state before this
    /// call: the outbox resolves the source now and fails when none provides the message. Sending a pending ID again
    /// with the same receiver coalesces with the pending message. A message addressed to the sending grain is rejected;
    /// send it through <see cref="Send(OutboxMessage)"/>, which stages it in the grain's own inbox.
    /// </remarks>
    /// <exception cref="ArgumentException">The ID is unset or too long, or the receiver is default or the sending grain.</exception>
    /// <exception cref="InvalidOperationException">
    /// The ID is pending as a different message, no registered source provides it, or the outbox is unavailable.
    /// </exception>
    void SendFromSource(HierarchicalKey messageId, GrainId receiverId);

    /// <summary>Registers a source which provides the envelopes of messages sent through <see cref="SendFromSource"/>.</summary>
    /// <param name="source">The source.</param>
    /// <returns>This outbox.</returns>
    /// <remarks>
    /// Register sources during construction or activation setup, before journal recovery; see
    /// <see cref="IOutboxMessageSource"/>.
    /// </remarks>
    IOutbox Register(IOutboxMessageSource source);
}
