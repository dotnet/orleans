namespace Orleans.Journaling.Messaging;

/// <summary>
/// Result of attempting to deliver a message to an inbox.
/// Struct for future extensibility (can add fields without breaking changes).
/// </summary>
[GenerateSerializer, Immutable]
public readonly struct DeliveryResult
{
    /// <summary>
    /// The status of the delivery attempt.
    /// </summary>
    [Id(0)]
    public DeliveryStatus Status { get; init; }

    /// <summary>
    /// For Processed status, contains the response envelope if a reply was sent.
    /// </summary>
    [Id(1)]
    public DurableEnvelope? Response { get; init; }

    /// <summary>
    /// Optional diagnostic message (e.g., reason for rejection).
    /// </summary>
    [Id(2)]
    public string? Message { get; init; }

    /// <summary>
    /// Creates a result indicating the message was accepted and persisted.
    /// </summary>
    public static DeliveryResult Accepted() => new() { Status = DeliveryStatus.Accepted };

    /// <summary>
    /// Creates a result indicating the message was a duplicate.
    /// </summary>
    public static DeliveryResult Duplicate() => new() { Status = DeliveryStatus.Duplicate };

    /// <summary>
    /// Creates a result indicating backpressure (inbox at capacity).
    /// </summary>
    public static DeliveryResult Backpressured() => new() { Status = DeliveryStatus.Backpressured };

    /// <summary>
    /// Creates a result indicating no handler was found for the route.
    /// </summary>
    /// <param name="routeKey">The route key that was not found.</param>
    public static DeliveryResult RouteNotFound(string routeKey) => new() { Status = DeliveryStatus.RouteNotFound, Message = $"No handler for route '{routeKey}'" };

    /// <summary>
    /// Creates a result indicating the message is pending (long-poll timeout).
    /// </summary>
    public static DeliveryResult Pending() => new() { Status = DeliveryStatus.Pending };

    /// <summary>
    /// Creates a result indicating the message was processed.
    /// </summary>
    /// <param name="response">Optional response envelope.</param>
    public static DeliveryResult Processed(DurableEnvelope? response = null) => new() { Status = DeliveryStatus.Processed, Response = response };
}
