#nullable enable
using System;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans streaming events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansStreamingDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for streaming events.
    /// </summary>
    public const string ListenerName = "Orleans.Streaming";

    /// <summary>
    /// Event names for streaming diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a stream message is delivered to a consumer.
        /// Payload: <see cref="StreamMessageDeliveredEvent"/>
        /// </summary>
        public const string MessageDelivered = "MessageDelivered";

        /// <summary>
        /// Event fired when a stream becomes inactive due to no activity.
        /// Payload: <see cref="StreamInactiveEvent"/>
        /// </summary>
        public const string StreamInactive = "StreamInactive";

        /// <summary>
        /// Event fired when a stream subscription is added.
        /// Payload: <see cref="StreamSubscriptionAddedEvent"/>
        /// </summary>
        public const string SubscriptionAdded = "SubscriptionAdded";

        /// <summary>
        /// Event fired when a stream subscription is removed.
        /// Payload: <see cref="StreamSubscriptionRemovedEvent"/>
        /// </summary>
        public const string SubscriptionRemoved = "SubscriptionRemoved";
    }
}

/// <summary>
/// Event payload for when a stream message is delivered to a consumer.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="SubscriptionId">The subscription ID of the consumer.</param>
/// <param name="ConsumerGrainId">The grain ID of the consumer.</param>
/// <param name="SequenceToken">The sequence token of the delivered message.</param>
/// <param name="SiloAddress">The address of the silo handling this delivery.</param>
public record StreamMessageDeliveredEvent(
    string StreamProvider,
    StreamId StreamId,
    Guid SubscriptionId,
    GrainId ConsumerGrainId,
    string? SequenceToken,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a stream becomes inactive due to no activity.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="InactivityPeriod">The configured inactivity period.</param>
/// <param name="SiloAddress">The address of the silo where this occurred.</param>
public record StreamInactiveEvent(
    string StreamProvider,
    StreamId StreamId,
    TimeSpan InactivityPeriod,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a stream subscription is added.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="SubscriptionId">The subscription ID.</param>
/// <param name="ConsumerGrainId">The grain ID of the consumer.</param>
/// <param name="SiloAddress">The address of the silo handling this subscription.</param>
public record StreamSubscriptionAddedEvent(
    string StreamProvider,
    StreamId StreamId,
    Guid SubscriptionId,
    GrainId ConsumerGrainId,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a stream subscription is removed.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="SubscriptionId">The subscription ID.</param>
/// <param name="SiloAddress">The address of the silo that handled this subscription.</param>
public record StreamSubscriptionRemovedEvent(
    string StreamProvider,
    StreamId StreamId,
    Guid SubscriptionId,
    SiloAddress? SiloAddress);
