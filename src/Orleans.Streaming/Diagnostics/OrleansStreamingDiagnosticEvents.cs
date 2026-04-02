using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

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
/// <param name="Consumer">The consumer endpoint.</param>
/// <param name="Batch">The delivered batch.</param>
public class StreamMessageDeliveredEvent(
    string StreamProvider,
    StreamId StreamId,
    Guid SubscriptionId,
    GrainId ConsumerGrainId,
    string? SequenceToken,
    SiloAddress? SiloAddress,
    IAddressable Consumer,
    IBatchContainer Batch)
{
    public string StreamProvider { get; } = StreamProvider;
    public StreamId StreamId { get; } = StreamId;
    public Guid SubscriptionId { get; } = SubscriptionId;
    public GrainId ConsumerGrainId { get; } = ConsumerGrainId;
    public string? SequenceToken { get; } = SequenceToken;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public IAddressable Consumer { get; } = Consumer;
    public IBatchContainer Batch { get; } = Batch;
}

/// <summary>
/// Event payload for when a stream becomes inactive due to no activity.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="InactivityPeriod">The configured inactivity period.</param>
/// <param name="SiloAddress">The address of the silo where this occurred.</param>
public class StreamInactiveEvent(
    string StreamProvider,
    StreamId StreamId,
    TimeSpan InactivityPeriod,
    SiloAddress? SiloAddress)
{
    public string StreamProvider { get; } = StreamProvider;
    public StreamId StreamId { get; } = StreamId;
    public TimeSpan InactivityPeriod { get; } = InactivityPeriod;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
}

/// <summary>
/// Event payload for when a stream subscription is added.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="SubscriptionId">The subscription ID.</param>
/// <param name="ConsumerGrainId">The grain ID of the consumer.</param>
/// <param name="SiloAddress">The address of the silo handling this subscription.</param>
public class StreamSubscriptionAddedEvent(
    string StreamProvider,
    StreamId StreamId,
    Guid SubscriptionId,
    GrainId ConsumerGrainId,
    SiloAddress? SiloAddress)
{
    public string StreamProvider { get; } = StreamProvider;
    public StreamId StreamId { get; } = StreamId;
    public Guid SubscriptionId { get; } = SubscriptionId;
    public GrainId ConsumerGrainId { get; } = ConsumerGrainId;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
}

/// <summary>
/// Event payload for when a stream subscription is removed.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="StreamId">The stream ID.</param>
/// <param name="SubscriptionId">The subscription ID.</param>
/// <param name="SiloAddress">The address of the silo that handled this subscription.</param>
public class StreamSubscriptionRemovedEvent(
    string StreamProvider,
    StreamId StreamId,
    Guid SubscriptionId,
    SiloAddress? SiloAddress)
{
    public string StreamProvider { get; } = StreamProvider;
    public StreamId StreamId { get; } = StreamId;
    public Guid SubscriptionId { get; } = SubscriptionId;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
}

/// <summary>
/// Event payload for when queue leases are acquired by a silo.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="SiloAddress">The address of the silo that acquired the leases.</param>
/// <param name="AcquiredQueueCount">The number of queues newly acquired.</param>
/// <param name="TotalQueueCount">The total number of queues now owned by this silo.</param>
/// <param name="TargetQueueCount">The target number of queues for this silo.</param>
/// <param name="QueueBalancer">The queue balancer instance.</param>
public class QueueLeasesAcquiredEvent(
    string StreamProvider,
    SiloAddress? SiloAddress,
    int AcquiredQueueCount,
    int TotalQueueCount,
    int TargetQueueCount,
    IStreamQueueBalancer QueueBalancer)
{
    public string StreamProvider { get; } = StreamProvider;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public int AcquiredQueueCount { get; } = AcquiredQueueCount;
    public int TotalQueueCount { get; } = TotalQueueCount;
    public int TargetQueueCount { get; } = TargetQueueCount;
    public IStreamQueueBalancer QueueBalancer { get; } = QueueBalancer;
}

/// <summary>
/// Event payload for when queue leases are released by a silo.
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="SiloAddress">The address of the silo that released the leases.</param>
/// <param name="ReleasedQueueCount">The number of queues released.</param>
/// <param name="TotalQueueCount">The total number of queues now owned by this silo.</param>
/// <param name="TargetQueueCount">The target number of queues for this silo.</param>
/// <param name="QueueBalancer">The queue balancer instance.</param>
public class QueueLeasesReleasedEvent(
    string StreamProvider,
    SiloAddress? SiloAddress,
    int ReleasedQueueCount,
    int TotalQueueCount,
    int TargetQueueCount,
    IStreamQueueBalancer QueueBalancer)
{
    public string StreamProvider { get; } = StreamProvider;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public int ReleasedQueueCount { get; } = ReleasedQueueCount;
    public int TotalQueueCount { get; } = TotalQueueCount;
    public int TargetQueueCount { get; } = TargetQueueCount;
    public IStreamQueueBalancer QueueBalancer { get; } = QueueBalancer;
}

/// <summary>
/// Event payload for when queue ownership changes (after rebalancing completes).
/// </summary>
/// <param name="StreamProvider">The name of the stream provider.</param>
/// <param name="SiloAddress">The address of the silo.</param>
/// <param name="OwnedQueueCount">The number of queues owned by this silo.</param>
/// <param name="TargetQueueCount">The target number of queues for this silo.</param>
/// <param name="ActiveSiloCount">The number of active silos in the cluster.</param>
/// <param name="QueueBalancer">The queue balancer instance.</param>
public class QueueBalancerChangedEvent(
    string StreamProvider,
    SiloAddress? SiloAddress,
    int OwnedQueueCount,
    int TargetQueueCount,
    int ActiveSiloCount,
    IStreamQueueBalancer QueueBalancer)
{
    public string StreamProvider { get; } = StreamProvider;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public int OwnedQueueCount { get; } = OwnedQueueCount;
    public int TargetQueueCount { get; } = TargetQueueCount;
    public int ActiveSiloCount { get; } = ActiveSiloCount;
    public IStreamQueueBalancer QueueBalancer { get; } = QueueBalancer;
}

internal static class OrleansStreamingDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansStreamingDiagnostics.ListenerName);

    internal static void EmitMessageDelivered(string streamProviderName, StreamConsumerData consumerData, IBatchContainer batch, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.MessageDelivered))
        {
            return;
        }

        Emit(Listener, streamProviderName, consumerData, batch, siloAddress);

        static void Emit(DiagnosticListener listener, string streamProviderName, StreamConsumerData consumerData, IBatchContainer batch, SiloAddress? siloAddress)
        {
            listener.Write(OrleansStreamingDiagnostics.EventNames.MessageDelivered, new StreamMessageDeliveredEvent(
                streamProviderName,
                consumerData.StreamId.StreamId,
                consumerData.SubscriptionId.Guid,
                consumerData.StreamConsumer.GetGrainId(),
                batch.SequenceToken?.ToString(),
                siloAddress,
                consumerData.StreamConsumer,
                batch));
        }
    }

    internal static void EmitStreamInactive(string streamProviderName, StreamId streamId, TimeSpan inactivityPeriod, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.StreamInactive))
        {
            return;
        }

        Emit(Listener, streamProviderName, streamId, inactivityPeriod, siloAddress);

        static void Emit(DiagnosticListener listener, string streamProviderName, StreamId streamId, TimeSpan inactivityPeriod, SiloAddress? siloAddress)
        {
            listener.Write(OrleansStreamingDiagnostics.EventNames.StreamInactive, new StreamInactiveEvent(
                streamProviderName,
                streamId,
                inactivityPeriod,
                siloAddress));
        }
    }

    internal static void EmitQueueChange(string streamProviderName, SiloAddress? siloAddress, int ownedQueueCount, int targetQueueCount, int activeSiloCount, HashSet<QueueId> oldQueues, HashSet<QueueId> newQueues, IStreamQueueBalancer queueBalancer)
    {
        if (!Listener.IsEnabled())
        {
            return;
        }

        Emit(Listener, streamProviderName, siloAddress, ownedQueueCount, targetQueueCount, activeSiloCount, oldQueues, newQueues, queueBalancer);

        static void Emit(DiagnosticListener listener, string streamProviderName, SiloAddress? siloAddress, int ownedQueueCount, int targetQueueCount, int activeSiloCount, HashSet<QueueId> oldQueues, HashSet<QueueId> newQueues, IStreamQueueBalancer queueBalancer)
        {
            if (listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged))
            {
                listener.Write(OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged, new QueueBalancerChangedEvent(
                    streamProviderName,
                    siloAddress,
                    ownedQueueCount,
                    targetQueueCount,
                    activeSiloCount,
                    queueBalancer));
            }

            if (listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueLeasesAcquired))
            {
                var acquired = newQueues.Except(oldQueues).Count();
                if (acquired > 0)
                {
                    listener.Write(OrleansStreamingDiagnostics.EventNames.QueueLeasesAcquired, new QueueLeasesAcquiredEvent(
                        streamProviderName,
                        siloAddress,
                        acquired,
                        ownedQueueCount,
                        targetQueueCount,
                        queueBalancer));
                }
            }

            if (listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueLeasesReleased))
            {
                var released = oldQueues.Except(newQueues).Count();
                if (released > 0)
                {
                    listener.Write(OrleansStreamingDiagnostics.EventNames.QueueLeasesReleased, new QueueLeasesReleasedEvent(
                        streamProviderName,
                        siloAddress,
                        released,
                        ownedQueueCount,
                        targetQueueCount,
                        queueBalancer));
                }
            }
        }
    }
}
