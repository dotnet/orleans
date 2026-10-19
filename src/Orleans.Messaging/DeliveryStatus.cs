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
    /// The inbox already holds the message, or processed it within the deduplication window.
    /// </summary>
    /// <remarks>
    /// A duplicate of a message which an inbox message sink rejected receives that rejection instead: the original
    /// <see cref="DeadLettered"/> result, with its reason and <see cref="DeliveryResult.Code"/>.
    /// </remarks>
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
    /// The receiver rejected the message permanently and moved it to its dead-letter store.
    /// </summary>
    /// <remarks>
    /// A redelivery of the message within the deduplication window receives the same result.
    /// </remarks>
    DeadLettered = 5
}
