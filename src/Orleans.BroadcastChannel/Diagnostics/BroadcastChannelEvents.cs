using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans broadcast channel events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class BroadcastChannelEvents
{
    /// <summary>
    /// The name of the diagnostic listener for broadcast channel events.
    /// </summary>
    public const string ListenerName = "Orleans.BroadcastChannel";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all broadcast channel events.
    /// </summary>
    public static IObservable<BroadcastChannelEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for broadcast channel diagnostic events.
    /// </summary>
    /// <param name="providerName">The name of the broadcast channel provider.</param>
    public abstract class BroadcastChannelEvent(string providerName)
    {
        /// <summary>
        /// The name of the broadcast channel provider.
        /// </summary>
        public readonly string ProviderName = providerName;
    }

    /// <summary>
    /// Event payload for when an item is published to a broadcast channel.
    /// </summary>
    /// <param name="providerName">The name of the broadcast channel provider.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="subscriberCount">The number of subscribers that will receive this item.</param>
    public sealed class ItemPublished(
        string providerName,
        ChannelId channelId,
        int subscriberCount) : BroadcastChannelEvent(providerName)
    {
        /// <summary>
        /// The channel ID.
        /// </summary>
        public readonly ChannelId ChannelId = channelId;

        /// <summary>
        /// The number of subscribers that will receive this item.
        /// </summary>
        public readonly int SubscriberCount = subscriberCount;
    }

    /// <summary>
    /// Event payload for when an item is delivered to a broadcast channel subscriber.
    /// </summary>
    /// <param name="providerName">The name of the broadcast channel provider.</param>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="consumerGrainId">The grain ID of the consumer.</param>
    public sealed class ItemDelivered(
        string providerName,
        ChannelId channelId,
        GrainId consumerGrainId) : BroadcastChannelEvent(providerName)
    {
        /// <summary>
        /// The channel ID.
        /// </summary>
        public readonly ChannelId ChannelId = channelId;

        /// <summary>
        /// The grain ID of the consumer.
        /// </summary>
        public readonly GrainId ConsumerGrainId = consumerGrainId;
    }

    internal static void EmitItemPublished(string providerName, ChannelId channelId, int subscriberCount)
    {
        if (!Listener.IsEnabled(nameof(ItemPublished)))
        {
            return;
        }

        Emit(providerName, channelId, subscriberCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string providerName, ChannelId channelId, int subscriberCount)
        {
            Listener.Write(nameof(ItemPublished), new ItemPublished(
                providerName,
                channelId,
                subscriberCount));
        }
    }

    internal static void EmitItemDelivered(string providerName, ChannelId channelId, GrainId consumerGrainId)
    {
        if (!Listener.IsEnabled(nameof(ItemDelivered)))
        {
            return;
        }

        Emit(providerName, channelId, consumerGrainId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string providerName, ChannelId channelId, GrainId consumerGrainId)
        {
            Listener.Write(nameof(ItemDelivered), new ItemDelivered(
                providerName,
                channelId,
                consumerGrainId));
        }
    }

    private sealed class Observable : IObservable<BroadcastChannelEvent>
    {
        public IDisposable Subscribe(IObserver<BroadcastChannelEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<BroadcastChannelEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is BroadcastChannelEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
