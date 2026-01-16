using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Durable outbox for sending messages.
/// Uses dictionary storage (no ordering guarantees).
/// </summary>
public interface IDurableOutbox
{
    /// <summary>
    /// Gets the number of pending outbound messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets all pending outbound messages.
    /// </summary>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Enqueues a message for delivery.
    /// </summary>
    /// <typeparam name="TBody">Body type.</typeparam>
    /// <param name="target">Target grain ID.</param>
    /// <param name="routeKey">Route key for target handler dispatch.</param>
    /// <param name="body">Message body.</param>
    /// <param name="correlationId">Optional correlation ID.</param>
    /// <param name="replyTo">Optional reply-to grain ID.</param>
    /// <param name="requestContext">Optional request context.</param>
    /// <returns>The message ID of the enqueued message.</returns>
    Guid Send<TBody>(
        GrainId target,
        string routeKey,
        TBody body,
        Guid? correlationId = null,
        GrainId? replyTo = null,
        Dictionary<string, object?>? requestContext = null);

    /// <summary>
    /// Removes a message after successful delivery.
    /// </summary>
    /// <param name="messageId">The message ID to remove.</param>
    /// <returns>true if the message was removed; otherwise, false.</returns>
    bool RemoveMessage(Guid messageId);

    /// <summary>
    /// Tries to get a specific outbox message.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="envelope">When this method returns true, contains the envelope.</param>
    /// <returns>true if the message exists; otherwise, false.</returns>
    bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);
}
