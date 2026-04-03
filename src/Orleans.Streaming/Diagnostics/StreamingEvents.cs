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

    internal static void EmitQueueChange(string streamProviderName, SiloAddress? siloAddress, HashSet<QueueId> oldQueues, HashSet<QueueId> newQueues, IStreamQueueBalancer queueBalancer)
    {
        if (!Listener.IsEnabled(nameof(BalancerChanged)))
        {
            return;
        }

        Emit(streamProviderName, siloAddress, oldQueues, newQueues, queueBalancer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            string streamProviderName,
            SiloAddress? siloAddress,
            HashSet<QueueId> oldQueues,
            HashSet<QueueId> newQueues,
            IStreamQueueBalancer queueBalancer)
        {
            Listener.Write(nameof(BalancerChanged), new BalancerChanged(
                streamProviderName,
                siloAddress,
                [.. oldQueues],
                [.. newQueues],
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
