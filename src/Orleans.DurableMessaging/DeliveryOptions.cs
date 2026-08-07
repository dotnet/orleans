using Orleans.Serialization;

namespace Orleans.DurableMessaging;

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
}
