using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Durable inbox for receiving and processing messages.
/// Uses dictionary storage (no ordering guarantees) which aids deduplication.
/// </summary>
public interface IDurableInbox
{
    /// <summary>
    /// Gets the number of unprocessed messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the maximum capacity. When reached, DeliverAsync returns Backpressured.
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
    /// <param name="envelope">When this method returns true, contains the envelope.</param>
    /// <returns>true if the message exists; otherwise, false.</returns>
    bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);

    /// <summary>
    /// Removes a message after processing.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>true if the message was removed; otherwise, false.</returns>
    bool RemoveMessage(GrainId senderId, Guid messageId);

    /// <summary>
    /// Checks if a message exists or has been processed.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>true if the message exists or has been processed; otherwise, false.</returns>
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
    /// <param name="routeKey">The route key to register.</param>
    /// <param name="handler">The handler for this route.</param>
    void RegisterHandler(string routeKey, IInboxHandler handler);

    /// <summary>
    /// Checks if a route has a registered handler.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>true if a handler is registered for this route; otherwise, false.</returns>
    bool HasHandler(string routeKey);
}
