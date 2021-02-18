using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams.Core;

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
    /// The extension multiplexes all stream related messages to this grain between different streams and their stream observers.
    /// 
    /// On the silo, we have one extension object per activation and this extension multiplexes all streams on this activation 
    ///     (streams of all types and ids: different stream ids and different stream providers).
    /// On the client, we have one extension per stream (we bind an extension for every StreamConsumer, therefore every stream has its own extension).
    /// </summary>
    [Serializable]
    internal class StreamConsumerExtension : IStreamConsumerExtension
    {
        private readonly IStreamProviderRuntime providerRuntime;
        private readonly ConcurrentDictionary<GuidId, IStreamSubscriptionHandle> allStreamObservers = new(); // map to different ObserversCollection<T> of different Ts.
        private readonly ILogger logger;
        private const int MAXIMUM_ITEM_STRING_LOG_LENGTH = 128;
        // if this extension is attached to a cosnumer grain which implements IOnSubscriptionActioner,
        // then this will be not null, otherwise, it will be null
        [NonSerialized]
        private readonly IStreamSubscriptionObserver streamSubscriptionObserver;

        internal StreamConsumerExtension(IStreamProviderRuntime providerRt, IStreamSubscriptionObserver streamSubscriptionObserver = null)
        {
            this.streamSubscriptionObserver = streamSubscriptionObserver;
            providerRuntime = providerRt;
            logger = providerRt.ServiceProvider.GetRequiredService<ILogger<StreamConsumerExtension>>();
        }

        internal StreamSubscriptionHandleImpl<T> SetObserver<T>(
            GuidId subscriptionId,
            StreamImpl<T> stream,
            IAsyncObserver<T> observer,
            IAsyncBatchObserver<T> batchObserver,
            StreamSequenceToken token,
            string filterData)
        {
            if (null == stream) throw new ArgumentNullException("stream");

            try
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("{0} AddObserver for stream {1}", providerRuntime.ExecutingEntityIdentity(), stream.InternalStreamId);

                // Note: The caller [StreamConsumer] already handles locking for Add/Remove operations, so we don't need to repeat here.
                var handle = new StreamSubscriptionHandleImpl<T>(subscriptionId, observer, batchObserver, stream, token, filterData);
                allStreamObservers[subscriptionId] = handle;
                return handle;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.StreamProvider_AddObserverException,
                    $"{providerRuntime.ExecutingEntityIdentity()} StreamConsumerExtension.AddObserver({stream.InternalStreamId}) caugth exception.", exc);
                throw;
            }
        }

        public bool RemoveObserver(GuidId subscriptionId)
        {
            return allStreamObservers.TryRemove(subscriptionId, out _);
        }

        public Task<StreamHandshakeToken> DeliverImmutable(GuidId subscriptionId, InternalStreamId streamId, Immutable<object> item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
        {
            return DeliverMutable(subscriptionId, streamId, item.Value, currentToken, handshakeToken);
        }

        public async Task<StreamHandshakeToken> DeliverMutable(GuidId subscriptionId, InternalStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                var itemString = item.ToString();
                itemString = (itemString.Length > MAXIMUM_ITEM_STRING_LOG_LENGTH) ? itemString.Substring(0, MAXIMUM_ITEM_STRING_LOG_LENGTH) + "..." : itemString;
                logger.Trace("DeliverItem {0} for subscription {1}", itemString, subscriptionId);
            }
            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
            {
                return await observer.DeliverItem(item, currentToken, handshakeToken);
            }
            else if(this.streamSubscriptionObserver != null)
            {
                var streamProvider = this.providerRuntime.ServiceProvider.GetServiceByName<IStreamProvider>(streamId.ProviderName);
                if(streamProvider != null)
                {
                    var subscriptionHandlerFactory = new StreamSubscriptionHandlerFactory(streamProvider, streamId, streamId.ProviderName, subscriptionId);
                    await this.streamSubscriptionObserver.OnSubscribed(subscriptionHandlerFactory);
                    //check if an observer were attached after handling the new subscription, deliver on it if attached
                    if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                    {
                        return await observer.DeliverItem(item, currentToken, handshakeToken);
                    }
                }
            }

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an item for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return default(StreamHandshakeToken);
        }

        public async Task<StreamHandshakeToken> DeliverBatch(GuidId subscriptionId, InternalStreamId streamId, Immutable<IBatchContainer> batch, StreamHandshakeToken handshakeToken)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("DeliverBatch {0} for subscription {1}", batch.Value, subscriptionId);

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
            {
                return await observer.DeliverBatch(batch.Value, handshakeToken);
            }
            else if(this.streamSubscriptionObserver != null)
            {
                var streamProvider = this.providerRuntime.ServiceProvider.GetServiceByName<IStreamProvider>(streamId.ProviderName);
                if (streamProvider != null)
                {
                    var subscriptionHandlerFactory = new StreamSubscriptionHandlerFactory(streamProvider, streamId, streamId.ProviderName, subscriptionId);
                    await this.streamSubscriptionObserver.OnSubscribed(subscriptionHandlerFactory);
                    // check if an observer were attached after handling the new subscription, deliver on it if attached
                    if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                    {
                        return await observer.DeliverBatch(batch.Value, handshakeToken);
                    }
                }
            }

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForBatch), "{0} got an item for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return default(StreamHandshakeToken);
        }

        public Task CompleteStream(GuidId subscriptionId)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("CompleteStream for subscription {0}", subscriptionId);

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                return observer.CompleteStream();

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got a Complete for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return Task.CompletedTask;
        }

        public Task ErrorInStream(GuidId subscriptionId, Exception exc)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("ErrorInStream {0} for subscription {1}", exc, subscriptionId);

            IStreamSubscriptionHandle observer;
            if (allStreamObservers.TryGetValue(subscriptionId, out observer))
                return observer.ErrorInStream(exc);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an Error for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return Task.CompletedTask;
        }

        public Task<StreamHandshakeToken> GetSequenceToken(GuidId subscriptionId)
        {
            IStreamSubscriptionHandle observer;
            return Task.FromResult(allStreamObservers.TryGetValue(subscriptionId, out observer) ? observer.GetSequenceToken() : null);
        }

        internal int DiagCountStreamObservers<T>(InternalStreamId streamId)
        {
            return allStreamObservers.Count(o => o.Value is StreamSubscriptionHandleImpl<T> i && i.SameStreamId(streamId));
        }

        internal List<StreamSubscriptionHandleImpl<T>> GetAllStreamHandles<T>()
        {
            var ls = new List<StreamSubscriptionHandleImpl<T>>();
            foreach (var o in allStreamObservers)
            {
                if (o.Value is StreamSubscriptionHandleImpl<T> i) ls.Add(i);
            }
            return ls;
        }
    }
}
