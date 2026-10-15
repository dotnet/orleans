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
    /// Gets the application-defined code which identifies why an inbox message sink dead-lettered the message, such as
    /// <c>"orders/duplicate-order"</c>; <see langword="null"/> when the sink reported none.
    /// </summary>
    /// <remarks>
    /// Messaging carries the code to the inbox dead letter and to the sender's
    /// <see cref="OutboxMessageRetirement.Code"/> without interpreting it.
    /// </remarks>
    [Id(2)]
    public string? Code { get; init; }

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
