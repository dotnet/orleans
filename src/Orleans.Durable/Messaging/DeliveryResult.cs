namespace Orleans.Messaging;

/// <summary>
/// The receiver's answer to an attempt to deliver a message to its inbox.
/// </summary>
/// <remarks>Case identifiers follow declaration order and are part of the wire format.</remarks>
[GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult")]
public union DeliveryResult(
    DeliveryResult.Accepted,
    DeliveryResult.Duplicate,
    DeliveryResult.Backpressured,
    DeliveryResult.HandlerNotFound,
    DeliveryResult.DeadLettered,
    DeliveryResult.Expired)
{
    /// <summary>The receiver durably accepted the message.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult.Accepted")]
    public sealed class Accepted
    {
        /// <summary>Gets the shared result.</summary>
        public static Accepted Instance { get; } = new();
    }

    /// <summary>The inbox already holds the message or retains its processed record.</summary>
    /// <remarks>
    /// A duplicate of a message which an inbox message sink rejected receives that rejection instead: the original
    /// <see cref="DeadLettered"/> result, with its reason and code.
    /// </remarks>
    [GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult.Duplicate")]
    public sealed class Duplicate
    {
        /// <summary>Gets the shared result.</summary>
        public static Duplicate Instance { get; } = new();
    }

    /// <summary>The inbox is at capacity; the sender retries later.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult.Backpressured")]
    public sealed class Backpressured
    {
        /// <summary>Gets the shared result.</summary>
        public static Backpressured Instance { get; } = new();
    }

    /// <summary>The receiving inbox has no registered handler.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult.HandlerNotFound")]
    public sealed class HandlerNotFound
    {
        /// <summary>Gets the shared result.</summary>
        public static HandlerNotFound Instance { get; } = new();
    }

    /// <summary>The receiver rejected the message permanently.</summary>
    /// <param name="reason">The diagnostic reason for the rejection.</param>
    /// <param name="code">The code which identifies why the receiver rejected the message, if any.</param>
    /// <remarks>A redelivery while its processed record is retained receives the same result.</remarks>
    [GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult.DeadLettered")]
    public sealed class DeadLettered(string reason, string? code = null)
    {
        /// <summary>Gets the diagnostic reason for the rejection.</summary>
        [Id(0)]
        public string Reason { get; } = reason;

        /// <summary>
        /// Gets the code which identifies why the receiver rejected the message: an application-defined code from an
        /// inbox message sink, such as <c>"orders/duplicate-order"</c>; <see langword="null"/> when the receiver
        /// reported none.
        /// </summary>
        /// <remarks>
        /// Messaging carries the code to the receiver's processed record and to the sender's
        /// <see cref="OutboxMessageRetirement.Rejected.Code"/> without interpreting it.
        /// </remarks>
        [Id(1)]
        public string? Code { get; } = code;
    }

    /// <summary>
    /// The message was sent at or before the receiver's durable admission floor, and the receiver retains no outcome
    /// for it.
    /// </summary>
    /// <remarks>
    /// This attempt stages no message effects. An earlier attempt can have been accepted, so the sender treats the
    /// delivery outcome as unknown and retires the message as <see cref="OutboxMessageRetirement.DeadLettered"/>.
    /// </remarks>
    [GenerateSerializer, Alias("Orleans.Messaging.DeliveryResult.Expired")]
    public sealed class Expired
    {
        /// <summary>Gets the shared result.</summary>
        public static Expired Instance { get; } = new();
    }
}
