using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableMessaging;

/// <summary>
/// Stages opaque durable messages alongside a grain's journaled business state.
/// </summary>
/// <remarks>
/// The journal capture hook establishes a durable self-wakeup before capturing pending state.
/// Dispatch begins after persistence acknowledgement. Messages remain pending until the destination
/// acknowledges durable acceptance, recognizes a duplicate, or reports a terminal delivery outcome.
/// Applications define ordering and business-operation idempotency in their protocols.
/// </remarks>
public interface IDurableOutbox
{
    /// <summary>
    /// Gets the number of pending outbound messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets pending messages in unspecified order.
    /// </summary>
    /// <remarks>Values are borrowed from durable state until removal or scope disposal.
    /// Do not dispose them; use <see cref="DurableEnvelope.Retain"/> for a longer lifetime.</remarks>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Synchronously stages an immutable envelope for the grain's next journal write.
    /// </summary>
    /// <param name="envelope">The fully prepared, borrowed outgoing envelope.</param>
    /// <remarks>
    /// Send does not consume the caller's ownership. Durable state retains an independent slice.
    /// The sender identity must match this outbox's grain. Equivalent repeated identities retain
    /// the original intent; conflicting identities fail explicitly. Inbox handlers stage outgoing
    /// messages in their synchronous final block before calling <see cref="IInboxHandlerContext.Complete"/>.
    /// Ordinary callers persist staged messages using their journaled state manager.
    /// An explicit write retry retains pending business changes and messages after a scheduling failure.
    /// </remarks>
    /// <exception cref="ArgumentException">The envelope has an empty identity.</exception>
    /// <exception cref="InvalidOperationException">
    /// The sender differs from the owning grain, the identity conflicts, or the outbox is unavailable.
    /// </exception>
    void Send(DurableEnvelope envelope);

    /// <summary>
    /// Looks up a pending outbound message.
    /// </summary>
    /// <param name="messageId">The message identifier.</param>
    /// <param name="envelope">The borrowed matching envelope when found.</param>
    /// <returns>Whether the message is pending.</returns>
    bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);
}
