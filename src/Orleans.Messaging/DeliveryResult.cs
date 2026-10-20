using Orleans.Serialization;

namespace Orleans.Messaging;

/// <summary>
/// Result of attempting to deliver a message to an inbox.
/// </summary>
[GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult")]
public readonly struct DeliveryResult
{
    /// <summary>
    /// The status of the delivery attempt.
    /// </summary>
    [Id(0)]
    public DeliveryStatus Status { get; init; }

    /// <summary>
    /// Optional diagnostic message (e.g., reason for rejection).
    /// </summary>
    [Id(1)]
    public string? Message { get; init; }

    /// <summary>
    /// Gets the code which identifies why the receiver dead-lettered the message: an application-defined code from an
    /// inbox message sink, such as <c>"orders/duplicate-order"</c>, or <see cref="DeduplicationWindowExceededCode"/>;
    /// <see langword="null"/> when the receiver reported none.
    /// </summary>
    /// <remarks>
    /// Messaging carries the code to the inbox dead letter and to the sender's
    /// <see cref="OutboxMessageRetirement.Code"/> without interpreting it.
    /// </remarks>
    [Id(2)]
    public string? Code { get; init; }

    /// <summary>
    /// The <see cref="Code"/> of a message which the receiver rejects because its
    /// <see cref="InboxMessage.RedeliveryDeadline"/> is later than the receiver's deduplication window would cover.
    /// </summary>
    /// <remarks>
    /// The receiver stages nothing for the message and answers every redelivery the same way, so the sending outbox keeps
    /// the message as a dead letter and retires it as <see cref="OutboxMessageRetirementStatus.Rejected"/>. It indicates
    /// that the sender's <c>OutboxRetryTimeout</c> isn't shorter than the receiver's <c>DeduplicationWindow</c>.
    /// </remarks>
    public const string DeduplicationWindowExceededCode = "DeduplicationWindowExceeded";

    /// <summary>
    /// Creates a result indicating the message was accepted and persisted to inbox.
    /// </summary>
    public static DeliveryResult Accepted() => new() { Status = DeliveryStatus.Accepted };

    /// <summary>
    /// Creates a result indicating the message was a duplicate.
    /// </summary>
    public static DeliveryResult Duplicate() => new() { Status = DeliveryStatus.Duplicate };

    /// <summary>
    /// Creates a result indicating the inbox is at capacity.
    /// </summary>
    public static DeliveryResult Backpressured() => new() { Status = DeliveryStatus.Backpressured };

    /// <summary>
    /// Creates a result indicating the receiving inbox has no registered handler.
    /// </summary>
    public static DeliveryResult HandlerNotFound() => new()
    {
        Status = DeliveryStatus.HandlerNotFound,
        Message = "No inbox handler is registered."
    };

    /// <summary>
    /// Creates a result indicating the message was dead-lettered.
    /// </summary>
    /// <param name="reason">The diagnostic reason.</param>
    /// <param name="code">The application-defined code which identifies the rejection, if any.</param>
    public static DeliveryResult DeadLettered(string reason, string? code = null) => new()
    {
        Status = DeliveryStatus.DeadLettered,
        Message = reason,
        Code = code
    };
}
