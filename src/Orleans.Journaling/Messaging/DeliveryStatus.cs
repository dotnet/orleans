namespace Orleans.Journaling.Messaging;

/// <summary>
/// Status codes for delivery attempts.
/// </summary>
public enum DeliveryStatus
{
    /// <summary>
    /// Message was accepted and persisted to inbox.
    /// </summary>
    Accepted,

    /// <summary>
    /// Message was a duplicate (already processed or in inbox).
    /// </summary>
    Duplicate,

    /// <summary>
    /// Inbox is at capacity; sender should retry later.
    /// </summary>
    Backpressured,

    /// <summary>
    /// No handler registered for the specified RouteKey.
    /// </summary>
    RouteNotFound,

    /// <summary>
    /// Message is pending processing (long-poll did not complete within timeout).
    /// </summary>
    Pending,

    /// <summary>
    /// Message was processed. Response may be included.
    /// </summary>
    Processed
}
