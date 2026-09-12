namespace Orleans.DurableMessaging;

/// <summary>
/// Status codes for delivery attempts.
/// </summary>
public enum DeliveryStatus
{
    /// <summary>
    /// Message was accepted and persisted to inbox.
    /// </summary>
    Accepted = 0,

    /// <summary>
    /// Message was a duplicate (already processed or in inbox).
    /// </summary>
    Duplicate = 1,

    /// <summary>
    /// Inbox is at capacity; sender should retry later.
    /// </summary>
    Backpressured = 2,

    /// <summary>
    /// No handler registered for the specified RouteKey.
    /// </summary>
    RouteNotFound = 3,

    /// <summary>
    /// The message was moved to the receiver's dead-letter store.
    /// </summary>
    DeadLettered = 6
}
