using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Durable inbox for receiving and processing messages.
/// Uses dictionary storage (no ordering guarantees) which aids deduplication.
/// </summary>
public interface IDurableInbox
{
    /// <summary>
    /// Number of unprocessed messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Maximum capacity. When reached, DeliverAsync returns Backpressured.
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Gets all pending messages (no ordering guarantee).
    /// </summary>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Tries to get a specific message by its key.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="envelope">The envelope if found.</param>
    /// <returns>True if the message exists in the inbox; otherwise, false.</returns>
    bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);

    /// <summary>
    /// Removes a message after processing.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>True if the message was removed; otherwise, false.</returns>
    bool RemoveMessage(GrainId senderId, Guid messageId);

    /// <summary>
    /// Checks if a message exists in the inbox or has been processed.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>True if the message is in the inbox or processed dictionary; otherwise, false.</returns>
    bool ContainsOrProcessed(GrainId senderId, Guid messageId);

    /// <summary>
    /// Marks a message as processed (for deduplication tracking).
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    void MarkProcessed(GrainId senderId, Guid messageId);

    /// <summary>
    /// Registers a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    void RegisterHandler(string routeKey, IInboxHandler handler);

    /// <summary>
    /// Checks if a route has a registered handler.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    bool HasHandler(string routeKey);

    /// <summary>
    /// Tries to get a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler);
}
