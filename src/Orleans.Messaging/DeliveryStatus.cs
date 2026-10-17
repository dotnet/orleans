namespace Orleans.Messaging;

/// <summary>
/// Status codes for delivery attempts.
/// </summary>
/// <remarks>Zero is not a valid status, so a default <see cref="DeliveryResult"/> reports no outcome.</remarks>
public enum DeliveryStatus
{
    /// <summary>
    /// Message was accepted and persisted to inbox.
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// Message was a duplicate (already processed or in inbox).
    /// </summary>
    Duplicate = 2,

    /// <summary>
    /// Inbox is at capacity; sender should retry later.
    /// </summary>
    Backpressured = 3,

    /// <summary>
    /// The receiving inbox has no registered handler.
    /// </summary>
    HandlerNotFound = 4,

    /// <summary>
    /// The message was moved to the receiver's dead-letter store.
    /// </summary>
    DeadLettered = 5
}
