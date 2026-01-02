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

        /// <summary>
        /// Event fired when queue leases are acquired by a silo.
        /// Payload: <see cref="QueueLeasesAcquiredEvent"/>
        /// </summary>
        public const string QueueLeasesAcquired = "QueueLeasesAcquired";

        /// <summary>
        /// Event fired when queue leases are released by a silo.
        /// Payload: <see cref="QueueLeasesReleasedEvent"/>
        /// </summary>
        public const string QueueLeasesReleased = "QueueLeasesReleased";

        /// <summary>
        /// Event fired when queue ownership changes (after rebalancing).
        /// Payload: <see cref="QueueBalancerChangedEvent"/>
        /// </summary>
        public const string QueueBalancerChanged = "QueueBalancerChanged";
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

/// <summary>
/// Event payload for when queue leases are acquired by a silo.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="SiloAddress">The address of the silo that acquired the leases.</param>
/// <param name="AcquiredQueueCount">The number of queues newly acquired.</param>
/// <param name="TotalQueueCount">The total number of queues now owned by this silo.</param>
/// <param name="TargetQueueCount">The target number of queues for this silo.</param>
public record QueueLeasesAcquiredEvent(
    string StreamProvider,
    SiloAddress? SiloAddress,
    int AcquiredQueueCount,
    int TotalQueueCount,
    int TargetQueueCount);

/// <summary>
/// Event payload for when queue leases are released by a silo.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="SiloAddress">The address of the silo that released the leases.</param>
/// <param name="ReleasedQueueCount">The number of queues released.</param>
/// <param name="TotalQueueCount">The total number of queues now owned by this silo.</param>
/// <param name="TargetQueueCount">The target number of queues for this silo.</param>
public record QueueLeasesReleasedEvent(
    string StreamProvider,
    SiloAddress? SiloAddress,
    int ReleasedQueueCount,
    int TotalQueueCount,
    int TargetQueueCount);

/// <summary>
/// Event payload for when queue ownership changes (after rebalancing completes).
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="SiloAddress">The address of the silo.</param>
/// <param name="OwnedQueueCount">The number of queues owned by this silo.</param>
/// <param name="TargetQueueCount">The target number of queues for this silo.</param>
/// <param name="ActiveSiloCount">The number of active silos in the cluster.</param>
public record QueueBalancerChangedEvent(
    string StreamProvider,
    SiloAddress? SiloAddress,
    int OwnedQueueCount,
    int TargetQueueCount,
    int ActiveSiloCount);
