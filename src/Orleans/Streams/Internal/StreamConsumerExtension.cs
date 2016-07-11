using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal interface IStreamSubscriptionHandle
    {
        Task<StreamHandshakeToken> DeliverItem(object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        Task<StreamHandshakeToken> DeliverBatch(IBatchContainer item, StreamHandshakeToken handshakeToken);
        Task CompleteStream();
        Task ErrorInStream(Exception exc);
        StreamHandshakeToken GetSequenceToken();
    }

    /// <summary>
    /// The extesion multiplexes all stream related messages to this grain between different streams and their stream observers.
    /// 
    /// On the silo, we have one extension object per activation and this extesion multiplexes all streams on this activation 
    ///     (streams of all types and ids: different stream ids and different stream providers).
    /// On the client, we have one extension per stream (we bind an extesion for every StreamConsumer, therefore every stream has its own extension).
    /// </summary>
    [Serializable]
    internal class StreamConsumerExtension : IStreamConsumerExtension
    {
        private readonly IStreamProviderRuntime providerRuntime;
        private readonly ConcurrentDictionary<GuidId, IStreamSubscriptionHandle> allStreamObservers; // map to different ObserversCollection<T> of different Ts.
        private readonly Logger logger;
        private readonly bool isRewindable;

        private const int MAXIMUM_ITEM_STRING_LOG_LENGTH = 128;

        internal StreamConsumerExtension(IStreamProviderRuntime providerRt, bool isRewindable)
        {
            providerRuntime = providerRt;
            allStreamObservers = new ConcurrentDictionary<GuidId, IStreamSubscriptionHandle>();
            this.isRewindable = isRewindable;
            logger = providerRuntime.GetLogger(GetType().Name);
        }

        internal StreamSubscriptionHandleImpl<T> SetObserver<T>(GuidId subscriptionId, StreamImpl<T> stream, IAsyncObserver<T> observer, StreamSequenceToken token, IStreamFilterPredicateWrapper filter)
        {
            if (null == stream) throw new ArgumentNullException("stream");
            if (null == observer) throw new ArgumentNullException("observer");

            try
            {
                if (logger.IsVerbose) logger.Verbose("{0} AddObserver for stream {1}", providerRuntime.ExecutingEntityIdentity(), stream.StreamId);

                // Note: The caller [StreamConsumer] already handles locking for Add/Remove operations, so we don't need to repeat here.
                var handle = new StreamSubscriptionHandleImpl<T>(subscriptionId, observer, stream, isRewindable, filter, token);
                return allStreamObservers.AddOrUpdate(subscriptionId, handle, (key, old) => handle) as StreamSubscriptionHandleImpl<T>;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.StreamProvider_AddObserverException,
                    $"{providerRuntime.ExecutingEntityIdentity()} StreamConsumerExtension.AddObserver({stream.StreamId}) caugth exception.", exc);
                throw;
            }
        }

        internal bool RemoveObserver(GuidId subscriptionId)
        {
            IStreamSubscriptionHandle ignore;
            return allStreamObservers.TryRemove(subscriptionId, out ignore);
        }

        public Task<StreamHandshakeToken> DeliverImmutable(GuidId subscriptionId, Immutable<object> item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
        {
            return DeliverMutable(subscriptionId, item.Value, currentToken, handshakeToken);
        }

        public Task<StreamHandshakeToken> DeliverMutable(GuidId subscriptionId, object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
        {
            if (logger.IsVerbose3)
            {
                var itemString = item.ToString();
                itemString = (itemString.Length > MAXIMUM_ITEM_STRING_LOG_LENGTH) ? itemString.Substring(0, MAXIMUM_ITEM_STRING_LOG_LENGTH) + "..." : itemString;
                logger.Verbose3("DeliverItem {0} for subscription {1}", itemString, subscriptionId);
            }

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                return observer.DeliverItem(item, currentToken, handshakeToken);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an item for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return Task.FromResult(default(StreamHandshakeToken));
        }

        public Task<StreamHandshakeToken> DeliverBatch(GuidId subscriptionId, Immutable<IBatchContainer> batch, StreamHandshakeToken handshakeToken)
        {
            if (logger.IsVerbose3) logger.Verbose3("DeliverBatch {0} for subscription {1}", batch.Value, subscriptionId);

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                return observer.DeliverBatch(batch.Value, handshakeToken);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForBatch), "{0} got an item for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return Task.FromResult(default(StreamHandshakeToken));
        }

        public Task CompleteStream(GuidId subscriptionId)
        {
            if (logger.IsVerbose3) logger.Verbose3("CompleteStream for subscription {0}", subscriptionId);

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                return observer.CompleteStream();

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got a Complete for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task ErrorInStream(GuidId subscriptionId, Exception exc)
        {
            if (logger.IsVerbose3) logger.Verbose3("ErrorInStream {0} for subscription {1}", exc, subscriptionId);

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                return observer.ErrorInStream(exc);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an Error for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task<StreamHandshakeToken> GetSequenceToken(GuidId subscriptionId)
        {
            IStreamSubscriptionHandle observer;
            return Task.FromResult(allStreamObservers.TryGetValue(subscriptionId, out observer) ? observer.GetSequenceToken() : null);
        }

        internal int DiagCountStreamObservers<T>(StreamId streamId)
        {
            return allStreamObservers.Values
                                     .OfType<StreamSubscriptionHandleImpl<T>>()
                                     .Aggregate(0, (count, o) => count + (o.SameStreamId(streamId) ? 1 : 0));
        }

        internal IList<StreamSubscriptionHandleImpl<T>> GetAllStreamHandles<T>()
        {
            return allStreamObservers.Values
                .OfType<StreamSubscriptionHandleImpl<T>>()
                .Where(o => o != null)
                .ToList();
        }
    }
}
