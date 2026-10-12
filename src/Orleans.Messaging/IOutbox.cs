using System;

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
    /// and destination must match. Custom headers do not change pending intent equality. Conflicts fail explicitly.
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
}
