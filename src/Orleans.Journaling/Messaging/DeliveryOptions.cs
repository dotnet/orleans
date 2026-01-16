namespace Orleans.Journaling.Messaging;

/// <summary>
/// Options for delivery, including long-polling configuration.
/// Modeled after SubscribeOrPollOptions in DurableTasks.
/// </summary>
[GenerateSerializer, Immutable]
public readonly struct DeliveryOptions()
{
    /// <summary>
    /// How long to wait for the message to be processed before returning Pending.
    /// Zero means return immediately after accepting/persisting.
    /// </summary>
    [Id(0)]
    public TimeSpan PollTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Optional observer to notify when processing completes (alternative to polling).
    /// </summary>
    [Id(1)]
    public GrainId? Observer { get; init; }
}
