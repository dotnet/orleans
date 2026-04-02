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
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="streamId">The stream ID.</param>
/// <param name="subscriptionId">The subscription ID of the consumer.</param>
/// <param name="siloAddress">The address of the silo handling this delivery.</param>
/// <param name="consumer">The consumer endpoint.</param>
/// <param name="batch">The delivered batch.</param>
public class StreamMessageDeliveredEvent(
    string streamProvider,
    StreamId streamId,
    Guid subscriptionId,
    SiloAddress? siloAddress,
    IAddressable consumer,
    IBatchContainer batch)
{
    public string StreamProvider { get; } = streamProvider;
    public StreamId StreamId { get; } = streamId;
    public Guid SubscriptionId { get; } = subscriptionId;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public IAddressable Consumer { get; } = consumer;
    public IBatchContainer Batch { get; } = batch;
}

/// <summary>
/// Event payload for when a stream becomes inactive due to no activity.
/// </summary>
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="streamId">The stream ID.</param>
/// <param name="inactivityPeriod">The configured inactivity period.</param>
/// <param name="siloAddress">The address of the silo where this occurred.</param>
public class StreamInactiveEvent(
    string streamProvider,
    StreamId streamId,
    TimeSpan inactivityPeriod,
    SiloAddress? siloAddress)
{
    public string StreamProvider { get; } = streamProvider;
    public StreamId StreamId { get; } = streamId;
    public TimeSpan InactivityPeriod { get; } = inactivityPeriod;
    public SiloAddress? SiloAddress { get; } = siloAddress;
}

/// <summary>
/// Event payload for when a stream subscription is added.
/// </summary>
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="streamId">The stream ID.</param>
/// <param name="subscriptionId">The subscription ID.</param>
/// <param name="consumerGrainId">The grain ID of the consumer.</param>
/// <param name="siloAddress">The address of the silo handling this subscription.</param>
public class StreamSubscriptionAddedEvent(
    string streamProvider,
    StreamId streamId,
    Guid subscriptionId,
    GrainId consumerGrainId,
    SiloAddress? siloAddress)
{
    public string StreamProvider { get; } = streamProvider;
    public StreamId StreamId { get; } = streamId;
    public Guid SubscriptionId { get; } = subscriptionId;
    public GrainId ConsumerGrainId { get; } = consumerGrainId;
    public SiloAddress? SiloAddress { get; } = siloAddress;
}

/// <summary>
/// Event payload for when a stream subscription is removed.
/// </summary>
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="streamId">The stream ID.</param>
/// <param name="subscriptionId">The subscription ID.</param>
/// <param name="siloAddress">The address of the silo that handled this subscription.</param>
public class StreamSubscriptionRemovedEvent(
    string streamProvider,
    StreamId streamId,
    Guid subscriptionId,
    SiloAddress? siloAddress)
{
    public string StreamProvider { get; } = streamProvider;
    public StreamId StreamId { get; } = streamId;
    public Guid SubscriptionId { get; } = subscriptionId;
    public SiloAddress? SiloAddress { get; } = siloAddress;
}

/// <summary>
/// Event payload for when queue leases are acquired by a silo.
/// </summary>
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="siloAddress">The address of the silo that acquired the leases.</param>
/// <param name="acquiredQueues">The queues newly acquired.</param>
/// <param name="queueBalancer">The queue balancer instance.</param>
public class QueueLeasesAcquiredEvent(
    string streamProvider,
    SiloAddress? siloAddress,
    QueueId[] acquiredQueues,
    IStreamQueueBalancer queueBalancer)
{
    public string StreamProvider { get; } = streamProvider;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public QueueId[] AcquiredQueues { get; } = acquiredQueues;
    public IStreamQueueBalancer QueueBalancer { get; } = queueBalancer;
}

/// <summary>
/// Event payload for when queue leases are released by a silo.
/// </summary>
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="siloAddress">The address of the silo that released the leases.</param>
/// <param name="releasedQueues">The queues which were released.</param>
/// <param name="queueBalancer">The queue balancer instance.</param>
public class QueueLeasesReleasedEvent(
    string streamProvider,
    SiloAddress? siloAddress,
    QueueId[] releasedQueues,
    IStreamQueueBalancer queueBalancer)
{
    public string StreamProvider { get; } = streamProvider;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public QueueId[] ReleasedQueues { get; } = releasedQueues;
    public IStreamQueueBalancer QueueBalancer { get; } = queueBalancer;
}

/// <summary>
/// Event payload for when queue ownership changes (after rebalancing completes).
/// </summary>
/// <param name="streamProvider">The name of the stream provider.</param>
/// <param name="siloAddress">The address of the silo.</param>
/// <param name="previousQueues">The queues owned before the change.</param>
/// <param name="currentQueues">The queues owned after the change.</param>
/// <param name="queueBalancer">The queue balancer instance.</param>
public class QueueBalancerChangedEvent(
    string streamProvider,
    SiloAddress? siloAddress,
    QueueId[] previousQueues,
    QueueId[] currentQueues,
    IStreamQueueBalancer queueBalancer)
{
    public string StreamProvider { get; } = streamProvider;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public QueueId[] PreviousQueues { get; } = previousQueues;
    public QueueId[] CurrentQueues { get; } = currentQueues;
    public IStreamQueueBalancer QueueBalancer { get; } = queueBalancer;
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

    internal static void EmitQueueChange(string streamProviderName, SiloAddress? siloAddress, HashSet<QueueId> oldQueues, HashSet<QueueId> newQueues, IStreamQueueBalancer queueBalancer)
    {
        if (!Listener.IsEnabled())
        {
            return;
        }

        Emit(Listener, streamProviderName, siloAddress, oldQueues, newQueues, queueBalancer);

        static void Emit(DiagnosticListener listener, string streamProviderName, SiloAddress? siloAddress, HashSet<QueueId> oldQueues, HashSet<QueueId> newQueues, IStreamQueueBalancer queueBalancer)
        {
            if (listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged))
            {
                listener.Write(OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged, new QueueBalancerChangedEvent(
                    streamProviderName,
                    siloAddress,
                    oldQueues.ToArray(),
                    newQueues.ToArray(),
                    queueBalancer));
            }

            if (listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueLeasesAcquired))
            {
                var acquired = newQueues.Except(oldQueues).ToArray();
                if (acquired.Length > 0)
                {
                    listener.Write(OrleansStreamingDiagnostics.EventNames.QueueLeasesAcquired, new QueueLeasesAcquiredEvent(
                        streamProviderName,
                        siloAddress,
                        acquired,
                        queueBalancer));
                }
            }

            if (listener.IsEnabled(OrleansStreamingDiagnostics.EventNames.QueueLeasesReleased))
            {
                var released = oldQueues.Except(newQueues).ToArray();
                if (released.Length > 0)
                {
                    listener.Write(OrleansStreamingDiagnostics.EventNames.QueueLeasesReleased, new QueueLeasesReleasedEvent(
                        streamProviderName,
                        siloAddress,
                        released,
                        queueBalancer));
                }
            }
        }
    }
}
