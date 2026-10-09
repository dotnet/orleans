using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Registers a grain's opaque-message handler and exposes pending inbox state.
/// </summary>
public interface IDurableInbox
{
    /// <summary>
    /// Gets the number of unprocessed messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the capacity at which delivery returns <see cref="DeliveryStatus.Backpressured"/>.
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Gets pending messages in unspecified order.
    /// </summary>
    /// <remarks>Values are borrowed from durable state until removal or scope disposal.
    /// Do not dispose them; use <see cref="DurableEnvelope.Retain"/> for a longer lifetime.</remarks>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Looks up a pending message by its transport identity.
    /// </summary>
    /// <param name="senderId">The original sender identity.</param>
    /// <param name="messageId">The message identifier.</param>
    /// <param name="envelope">The borrowed matching envelope when found.</param>
    /// <returns>Whether the message is pending.</returns>
    bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);

    /// <summary>
    /// Registers the handler for this inbox.
    /// </summary>
    /// <param name="handler">The handler responsible for application decoding and dispatch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A handler has already been registered.</exception>
    void RegisterHandler(IInboxHandler handler);
}
