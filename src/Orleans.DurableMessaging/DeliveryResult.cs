using Orleans.Serialization;

namespace Orleans.DurableMessaging;

/// <summary>
/// Result of attempting to deliver a message to an inbox.
/// </summary>
[GenerateSerializer, Alias("Orleans.DurableMessaging.DeliveryResult")]
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
    public static DeliveryResult DeadLettered(string reason) => new()
    {
        Status = DeliveryStatus.DeadLettered,
        Message = reason
    };
}
