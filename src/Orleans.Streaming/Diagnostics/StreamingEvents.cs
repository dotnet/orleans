using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans streaming events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class StreamingEvents
{
    /// <summary>
    /// The name of the diagnostic listener for streaming events.
    /// </summary>
    public const string ListenerName = "Orleans.Streaming";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all streaming events.
    /// </summary>
    public static IObservable<StreamingEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for streaming diagnostic events.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo associated with the event, if any.</param>
    public abstract class StreamingEvent(
        string streamProvider,
        SiloAddress? siloAddress)
    {
        /// <summary>
        /// The name of the stream provider.
        /// </summary>
        public readonly string StreamProvider = streamProvider;

        /// <summary>
        /// The address of the silo associated with the event, if any.
        /// </summary>
        public readonly SiloAddress? SiloAddress = siloAddress;
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
    public sealed class MessageDelivered(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        SiloAddress? siloAddress,
        IAddressable consumer,
        IBatchContainer batch) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID of the consumer.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;

        /// <summary>
        /// The consumer endpoint.
        /// </summary>
        public readonly IAddressable Consumer = consumer;

        /// <summary>
        /// The delivered batch.
        /// </summary>
        public readonly IBatchContainer Batch = batch;
    }

    /// <summary>
    /// Event payload for when a stream becomes inactive due to no activity.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="inactivityPeriod">The configured inactivity period.</param>
    /// <param name="siloAddress">The address of the silo where this occurred.</param>
    public sealed class StreamInactive(
        string streamProvider,
        StreamId streamId,
        TimeSpan inactivityPeriod,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The configured inactivity period.
        /// </summary>
        public readonly TimeSpan InactivityPeriod = inactivityPeriod;
    }

    /// <summary>
    /// Event payload for when a stream subscription is added.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="consumerGrainId">The grain ID of the consumer.</param>
    /// <param name="siloAddress">The address of the silo handling this subscription.</param>
    public sealed class SubscriptionAdded(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        GrainId consumerGrainId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;

        /// <summary>
        /// The grain ID of the consumer.
        /// </summary>
        public readonly GrainId ConsumerGrainId = consumerGrainId;
    }

    /// <summary>
    /// Event payload for when a stream subscription is durably registered in pubsub state.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="consumerGrainId">The grain ID of the consumer.</param>
    /// <param name="siloAddress">The address of the silo handling this registration.</param>
    public sealed class SubscriptionRegistered(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        GrainId consumerGrainId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;

        /// <summary>
        /// The grain ID of the consumer.
        /// </summary>
        public readonly GrainId ConsumerGrainId = consumerGrainId;
    }

    /// <summary>
    /// Event payload for when a stream subscription is attached to a pulling agent and ready to receive data.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="consumerGrainId">The grain ID of the consumer.</param>
    /// <param name="siloAddress">The address of the silo handling this attachment.</param>
    public sealed class SubscriptionAttached(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        GrainId consumerGrainId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;

        /// <summary>
        /// The grain ID of the consumer.
        /// </summary>
        public readonly GrainId ConsumerGrainId = consumerGrainId;
    }

    /// <summary>
     /// Event payload for when a stream subscription is removed.
     /// </summary>
     /// <param name="streamProvider">The name of the stream provider.</param>
     /// <param name="streamId">The stream ID.</param>
     /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="siloAddress">The address of the silo that handled this subscription.</param>
    public sealed class SubscriptionRemoved(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;
    }

    /// <summary>
    /// Event payload for when a stream subscription is durably removed from pubsub state.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="siloAddress">The address of the silo handling this removal.</param>
    public sealed class SubscriptionUnregistered(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;
    }

    /// <summary>
    /// Event payload for when a stream subscription is detached from a pulling agent.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="siloAddress">The address of the silo handling this detachment.</param>
    public sealed class SubscriptionDetached(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;
    }

    /// <summary>
    /// Event payload for when a producer is durably registered in pubsub state for a stream.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="producerGrainId">The producer grain ID.</param>
    /// <param name="siloAddress">The address of the silo handling this registration.</param>
    public sealed class ProducerRegistered(
        string streamProvider,
        StreamId streamId,
        GrainId producerGrainId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The producer grain ID.
        /// </summary>
        public readonly GrainId ProducerGrainId = producerGrainId;
    }

    /// <summary>
    /// Event payload for when a producer is durably removed from pubsub state for a stream.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="producerGrainId">The producer grain ID.</param>
    /// <param name="siloAddress">The address of the silo handling this removal.</param>
    public sealed class ProducerUnregistered(
        string streamProvider,
        StreamId streamId,
        GrainId producerGrainId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The producer grain ID.
        /// </summary>
        public readonly GrainId ProducerGrainId = producerGrainId;
    }

    /// <summary>
    /// Event payload for when a consumer cursor is drained and no more currently available work remains.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <param name="siloAddress">The address of the silo handling this cursor.</param>
    public sealed class ConsumerCursorDrained(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        SiloAddress? siloAddress) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;
    }

    /// <summary>
     /// Event payload for when an individual item from a stream batch is delivered to a consumer.
     /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="subscriptionId">The subscription ID of the consumer.</param>
    /// <param name="siloAddress">The address of the silo handling this delivery.</param>
    /// <param name="clusterId">The identifier of the cluster handling this delivery.</param>
    /// <param name="sequenceToken">The sequence token of the delivered item.</param>
    public sealed class ItemDelivered(
        string streamProvider,
        StreamId streamId,
        Guid subscriptionId,
        SiloAddress? siloAddress,
        string clusterId,
        StreamSequenceToken? sequenceToken) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// Initializes an event without cluster identity.
        /// </summary>
        public ItemDelivered(
            string streamProvider,
            StreamId streamId,
            Guid subscriptionId,
            SiloAddress? siloAddress,
            StreamSequenceToken? sequenceToken)
            : this(streamProvider, streamId, subscriptionId, siloAddress, clusterId: string.Empty, sequenceToken)
        {
        }

        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The subscription ID of the consumer.
        /// </summary>
        public readonly Guid SubscriptionId = subscriptionId;

        /// <summary>
        /// The identifier of the cluster associated with the delivery.
        /// </summary>
        public readonly string ClusterId = clusterId;

        /// <summary>
        /// The sequence token of the delivered item.
        /// </summary>
        public readonly StreamSequenceToken? SequenceToken = sequenceToken;
    }

    /// <summary>
    /// Event payload for when queue ownership changes after rebalancing completes.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="previousQueues">The queues owned before the change.</param>
    /// <param name="currentQueues">The queues owned after the change.</param>
    /// <param name="queueBalancer">The queue balancer instance.</param>
    public sealed class BalancerChanged(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId[] previousQueues,
        QueueId[] currentQueues,
        IStreamQueueBalancer queueBalancer) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queues owned before the change.
        /// </summary>
        public readonly QueueId[] PreviousQueues = previousQueues;

        /// <summary>
        /// The queues owned after the change.
        /// </summary>
        public readonly QueueId[] CurrentQueues = currentQueues;

        /// <summary>
        /// The queue balancer instance.
        /// </summary>
        public readonly IStreamQueueBalancer QueueBalancer = queueBalancer;
    }

    /// <summary>
    /// Event payload for when a pulling agent manager reports its current queue assignments.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="currentQueues">The queues currently owned by the manager.</param>
    /// <param name="runningAgents">The number of running pulling agents.</param>
    public sealed class PullingAgentManagerState(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId[] currentQueues,
        int runningAgents) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queues currently owned by the manager.
        /// </summary>
        public readonly QueueId[] CurrentQueues = currentQueues;

        /// <summary>
        /// The number of running pulling agents.
        /// </summary>
        public readonly int RunningAgents = runningAgents;
    }

    /// <summary>
    /// Event payload for when a deployment-based queue balancer completes a silo maturity transition.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo running the queue balancer.</param>
    /// <param name="maturedSiloAddress">The silo whose maturity period completed.</param>
    /// <param name="isLocalSilo">Whether the matured silo is the local silo.</param>
    /// <param name="queueBalancer">The queue balancer instance.</param>
    public sealed class QueueBalancerMaturityCompleted(
        string streamProvider,
        SiloAddress? siloAddress,
        SiloAddress maturedSiloAddress,
        bool isLocalSilo,
        IStreamQueueBalancer queueBalancer) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The silo whose maturity period completed.
        /// </summary>
        public readonly SiloAddress MaturedSiloAddress = maturedSiloAddress;

        /// <summary>
        /// Whether the matured silo is the local silo.
        /// </summary>
        public readonly bool IsLocalSilo = isLocalSilo;

        /// <summary>
        /// The queue balancer instance.
        /// </summary>
        public readonly IStreamQueueBalancer QueueBalancer = queueBalancer;
    }

    /// <summary>
    /// Event payload for when a persistent stream pulling agent starts for a queue.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="queueId">The queue handled by the pulling agent.</param>
    /// <param name="dueTime">The initial due time for the queue pump timer.</param>
    /// <param name="period">The queue pump timer period.</param>
    public sealed class PullingAgentStarted(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId queueId,
        TimeSpan dueTime,
        TimeSpan period) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queue handled by the pulling agent.
        /// </summary>
        public readonly QueueId QueueId = queueId;

        /// <summary>
        /// The initial due time for the queue pump timer.
        /// </summary>
        public readonly TimeSpan DueTime = dueTime;

        /// <summary>
        /// The queue pump timer period.
        /// </summary>
        public readonly TimeSpan Period = period;
    }

    /// <summary>
    /// Event payload for when a persistent stream pulling agent stops for a queue.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="queueId">The queue handled by the pulling agent.</param>
    public sealed class PullingAgentStopped(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId queueId) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queue handled by the pulling agent.
        /// </summary>
        public readonly QueueId QueueId = queueId;
    }

    /// <summary>
    /// Event payload for when a persistent stream pulling agent queue receiver initializes.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="queueId">The initialized queue.</param>
    public sealed class QueueReceiverInitialized(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId queueId) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The initialized queue.
        /// </summary>
        public readonly QueueId QueueId = queueId;
    }

    /// <summary>
    /// Event payload for when a persistent stream pulling agent queue receiver fails to initialize.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="queueId">The queue which failed to initialize.</param>
    /// <param name="exception">The initialization exception.</param>
    public sealed class QueueReceiverInitializationFailed(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId queueId,
        Exception exception) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queue which failed to initialize.
        /// </summary>
        public readonly QueueId QueueId = queueId;

        /// <summary>
        /// The initialization exception.
        /// </summary>
        public readonly Exception Exception = exception;
    }

    /// <summary>
    /// Event payload for when a persistent stream pulling agent registers a local stream entry.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="queueId">The queue handled by the pulling agent.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="producerGrainId">The pulling agent grain ID registered as producer in pubsub.</param>
    /// <param name="subscriberCount">The number of non-faulted subscribers returned by pubsub.</param>
    public sealed class PullingAgentStreamRegistered(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId queueId,
        StreamId streamId,
        GrainId producerGrainId,
        int subscriberCount) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queue handled by the pulling agent.
        /// </summary>
        public readonly QueueId QueueId = queueId;

        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The pulling agent grain ID registered as producer in pubsub.
        /// </summary>
        public readonly GrainId ProducerGrainId = producerGrainId;

        /// <summary>
        /// The number of non-faulted subscribers returned by pubsub.
        /// </summary>
        public readonly int SubscriberCount = subscriberCount;
    }

    /// <summary>
    /// Event payload for when a persistent stream pulling agent fails to register a local stream entry.
    /// </summary>
    /// <param name="streamProvider">The name of the stream provider.</param>
    /// <param name="siloAddress">The address of the silo.</param>
    /// <param name="queueId">The queue handled by the pulling agent.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="exception">The registration exception.</param>
    public sealed class PullingAgentStreamRegistrationFailed(
        string streamProvider,
        SiloAddress? siloAddress,
        QueueId queueId,
        StreamId streamId,
        Exception exception) : StreamingEvent(streamProvider, siloAddress)
    {
        /// <summary>
        /// The queue handled by the pulling agent.
        /// </summary>
        public readonly QueueId QueueId = queueId;

        /// <summary>
        /// The stream ID.
        /// </summary>
        public readonly StreamId StreamId = streamId;

        /// <summary>
        /// The registration exception.
        /// </summary>
        public readonly Exception Exception = exception;
    }

    internal static bool IsPullingAgentStartedEnabled() => Listener.IsEnabled(nameof(PullingAgentStarted));

    internal static void EmitPullingAgentStarted(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, TimeSpan dueTime, TimeSpan period)
    {
        if (!IsPullingAgentStartedEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, queueId, dueTime, period);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, TimeSpan dueTime, TimeSpan period)
        {
            Listener.Write(nameof(PullingAgentStarted), new PullingAgentStarted(
                streamProviderName,
                siloAddress,
                queueId,
                dueTime,
                period));
        }
    }

    internal static bool IsPullingAgentStoppedEnabled() => Listener.IsEnabled(nameof(PullingAgentStopped));

    internal static void EmitPullingAgentStopped(string streamProviderName, SiloAddress? siloAddress, QueueId queueId)
    {
        if (!IsPullingAgentStoppedEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, queueId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, QueueId queueId)
        {
            Listener.Write(nameof(PullingAgentStopped), new PullingAgentStopped(
                streamProviderName,
                siloAddress,
                queueId));
        }
    }

    internal static bool IsQueueReceiverInitializedEnabled() => Listener.IsEnabled(nameof(QueueReceiverInitialized));

    internal static void EmitQueueReceiverInitialized(string streamProviderName, SiloAddress? siloAddress, QueueId queueId)
    {
        if (!IsQueueReceiverInitializedEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, queueId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, QueueId queueId)
        {
            Listener.Write(nameof(QueueReceiverInitialized), new QueueReceiverInitialized(
                streamProviderName,
                siloAddress,
                queueId));
        }
    }

    internal static bool IsQueueReceiverInitializationFailedEnabled() => Listener.IsEnabled(nameof(QueueReceiverInitializationFailed));

    internal static void EmitQueueReceiverInitializationFailed(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, Exception exception)
    {
        if (!IsQueueReceiverInitializationFailedEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, queueId, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, Exception exception)
        {
            Listener.Write(nameof(QueueReceiverInitializationFailed), new QueueReceiverInitializationFailed(
                streamProviderName,
                siloAddress,
                queueId,
                exception));
        }
    }

    internal static bool IsPullingAgentStreamRegisteredEnabled() => Listener.IsEnabled(nameof(PullingAgentStreamRegistered));

    internal static void EmitPullingAgentStreamRegistered(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, StreamId streamId, GrainId producerGrainId, int subscriberCount)
    {
        if (!IsPullingAgentStreamRegisteredEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, queueId, streamId, producerGrainId, subscriberCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, StreamId streamId, GrainId producerGrainId, int subscriberCount)
        {
            Listener.Write(nameof(PullingAgentStreamRegistered), new PullingAgentStreamRegistered(
                streamProviderName,
                siloAddress,
                queueId,
                streamId,
                producerGrainId,
                subscriberCount));
        }
    }

    internal static bool IsPullingAgentStreamRegistrationFailedEnabled() => Listener.IsEnabled(nameof(PullingAgentStreamRegistrationFailed));

    internal static void EmitPullingAgentStreamRegistrationFailed(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, StreamId streamId, Exception exception)
    {
        if (!IsPullingAgentStreamRegistrationFailedEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, queueId, streamId, exception);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, QueueId queueId, StreamId streamId, Exception exception)
        {
            Listener.Write(nameof(PullingAgentStreamRegistrationFailed), new PullingAgentStreamRegistrationFailed(
                streamProviderName,
                siloAddress,
                queueId,
                streamId,
                exception));
        }
    }

    internal static void EmitMessageDelivered(string streamProviderName, StreamConsumerData consumerData, IBatchContainer batch, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(MessageDelivered)))
        {
            return;
        }

        Emit(streamProviderName, consumerData, batch, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamConsumerData consumerData, IBatchContainer batch, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(MessageDelivered), new MessageDelivered(
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
        if (!Listener.IsEnabled(nameof(StreamInactive)))
        {
            return;
        }

        Emit(streamProviderName, streamId, inactivityPeriod, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, TimeSpan inactivityPeriod, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(StreamInactive), new StreamInactive(
                streamProviderName,
                streamId,
                inactivityPeriod,
                siloAddress));
        }
    }

    internal static void EmitSubscriptionAdded(string streamProviderName, StreamId streamId, Guid subscriptionId, GrainId consumerGrainId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SubscriptionAdded)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, consumerGrainId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, GrainId consumerGrainId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(SubscriptionAdded), new SubscriptionAdded(
                streamProviderName,
                streamId,
                subscriptionId,
                consumerGrainId,
                siloAddress));
        }
    }

    internal static void EmitSubscriptionRegistered(string streamProviderName, StreamId streamId, Guid subscriptionId, GrainId consumerGrainId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SubscriptionRegistered)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, consumerGrainId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, GrainId consumerGrainId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(SubscriptionRegistered), new SubscriptionRegistered(
                streamProviderName,
                streamId,
                subscriptionId,
                consumerGrainId,
                siloAddress));
        }
    }

    internal static void EmitSubscriptionAttached(string streamProviderName, StreamId streamId, Guid subscriptionId, GrainId consumerGrainId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SubscriptionAttached)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, consumerGrainId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, GrainId consumerGrainId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(SubscriptionAttached), new SubscriptionAttached(
                streamProviderName,
                streamId,
                subscriptionId,
                consumerGrainId,
                siloAddress));
        }
    }

    internal static void EmitSubscriptionRemoved(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SubscriptionRemoved)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(SubscriptionRemoved), new SubscriptionRemoved(
                streamProviderName,
                streamId,
                subscriptionId,
                siloAddress));
        }
    }

    internal static void EmitSubscriptionUnregistered(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SubscriptionUnregistered)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(SubscriptionUnregistered), new SubscriptionUnregistered(
                streamProviderName,
                streamId,
                subscriptionId,
                siloAddress));
        }
    }

    internal static void EmitSubscriptionDetached(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(SubscriptionDetached)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(SubscriptionDetached), new SubscriptionDetached(
                streamProviderName,
                streamId,
                subscriptionId,
                siloAddress));
        }
    }

    internal static void EmitProducerRegistered(string streamProviderName, StreamId streamId, GrainId producerGrainId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(ProducerRegistered)))
        {
            return;
        }

        Emit(streamProviderName, streamId, producerGrainId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, GrainId producerGrainId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(ProducerRegistered), new ProducerRegistered(
                streamProviderName,
                streamId,
                producerGrainId,
                siloAddress));
        }
    }

    internal static void EmitProducerUnregistered(string streamProviderName, StreamId streamId, GrainId producerGrainId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(ProducerUnregistered)))
        {
            return;
        }

        Emit(streamProviderName, streamId, producerGrainId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, GrainId producerGrainId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(ProducerUnregistered), new ProducerUnregistered(
                streamProviderName,
                streamId,
                producerGrainId,
                siloAddress));
        }
    }

    internal static void EmitConsumerCursorDrained(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(ConsumerCursorDrained)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(ConsumerCursorDrained), new ConsumerCursorDrained(
                streamProviderName,
                streamId,
                subscriptionId,
                siloAddress));
        }
    }

    internal static void EmitItemDelivered(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress, string clusterId, StreamSequenceToken? sequenceToken)
    {
        if (!Listener.IsEnabled(nameof(ItemDelivered)))
        {
            return;
        }

        Emit(streamProviderName, streamId, subscriptionId, siloAddress, clusterId, sequenceToken);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, StreamId streamId, Guid subscriptionId, SiloAddress? siloAddress, string clusterId, StreamSequenceToken? sequenceToken)
        {
            Listener.Write(nameof(ItemDelivered), new ItemDelivered(
                streamProviderName,
                streamId,
                subscriptionId,
                siloAddress,
                clusterId,
                sequenceToken));
        }
    }

    internal static bool IsBalancerChangedEnabled() => Listener.IsEnabled(nameof(BalancerChanged));

    internal static void EmitQueueChange(string streamProviderName, SiloAddress? siloAddress, QueueId[] oldQueues, QueueId[] newQueues, IStreamQueueBalancer queueBalancer)
    {
        if (!IsBalancerChangedEnabled())
        {
            return;
        }

        Emit(streamProviderName, siloAddress, oldQueues, newQueues, queueBalancer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            string streamProviderName,
            SiloAddress? siloAddress,
            QueueId[] oldQueues,
            QueueId[] newQueues,
            IStreamQueueBalancer queueBalancer)
        {
            Listener.Write(nameof(BalancerChanged), new BalancerChanged(
                streamProviderName,
                siloAddress,
                oldQueues,
                newQueues,
                queueBalancer));
        }
    }

    internal static void EmitPullingAgentManagerState(string streamProviderName, SiloAddress? siloAddress, IEnumerable<QueueId> currentQueues, int runningAgents)
    {
        if (!Listener.IsEnabled(nameof(PullingAgentManagerState)))
        {
            return;
        }

        Emit(streamProviderName, siloAddress, currentQueues, runningAgents);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string streamProviderName, SiloAddress? siloAddress, IEnumerable<QueueId> currentQueues, int runningAgents)
        {
            Listener.Write(nameof(PullingAgentManagerState), new PullingAgentManagerState(
                streamProviderName,
                siloAddress,
                [.. currentQueues],
                runningAgents));
        }
    }

    internal static void EmitQueueBalancerMaturityCompleted(
        string streamProviderName,
        SiloAddress? siloAddress,
        SiloAddress maturedSiloAddress,
        bool isLocalSilo,
        IStreamQueueBalancer queueBalancer)
    {
        if (!Listener.IsEnabled(nameof(QueueBalancerMaturityCompleted)))
        {
            return;
        }

        Emit(streamProviderName, siloAddress, maturedSiloAddress, isLocalSilo, queueBalancer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            string streamProviderName,
            SiloAddress? siloAddress,
            SiloAddress maturedSiloAddress,
            bool isLocalSilo,
            IStreamQueueBalancer queueBalancer)
        {
            Listener.Write(nameof(QueueBalancerMaturityCompleted), new QueueBalancerMaturityCompleted(
                streamProviderName,
                siloAddress,
                maturedSiloAddress,
                isLocalSilo,
                queueBalancer));
        }
    }

    private sealed class Observable : IObservable<StreamingEvent>
    {
        public IDisposable Subscribe(IObserver<StreamingEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<StreamingEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is StreamingEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
