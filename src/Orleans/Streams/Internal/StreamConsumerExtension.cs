/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Security.Principal;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
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
        private interface IStreamObservers
        {
            Streams.StreamSequenceToken Token { get; }
            Task<StreamSequenceToken> DeliverItem(object item, StreamSequenceToken token);
            Task<StreamSequenceToken> DeliverBatch(IBatchContainer item);
            Task CompleteStream();
            Task ErrorInStream(Exception exc);
        }

        [Serializable]
        private class ObserversCollection<T> : IStreamObservers
        {
            private StreamSubscriptionHandleImpl<T> localObserver;
            private bool dirty = false;
            private StreamSequenceToken expectedToken;

            public StreamSequenceToken Token
            {
                get { return expectedToken; }
            }

            internal void SetObserver(StreamSubscriptionHandleImpl<T> observer, StreamSequenceToken token)
            {
                localObserver = observer;
                this.expectedToken = token;
                dirty = true;
            }

            internal void RemoveObserver()
            {
                localObserver = null;
                dirty = false;
            }

            internal bool IsEmpty
            {
                get { return localObserver == null; }
            }

            public async Task<StreamSequenceToken> DeliverItem(object item, StreamSequenceToken token)
            {
                if (dirty)
                {
                    dirty = false;
                    if (expectedToken != null)
                    {
                        return expectedToken;
                    }
                }

                T typedItem;
                try
                {
                    typedItem = (T)item;
                }
                catch (InvalidCastException)
                {
                    // We got an illegal item on the stream -- close it with a Cast exception
                    throw new InvalidCastException("Received an item of type " + item.GetType().Name + ", expected " + typeof(T).FullName);
                }

                if (localObserver != null)
                {
                    await localObserver.OnNextAsync(typedItem, token);
                }

                if (dirty)
                {
                    dirty = false;
                    if (expectedToken != null)
                    {
                        return expectedToken;
                    }
                }

                if (token != null && token.Newer(expectedToken))
                {
                    expectedToken = token;
                }

                return default(StreamSequenceToken);
            }

            public async Task<StreamSequenceToken> DeliverBatch(IBatchContainer batch)
            {
                foreach (var itemTuple in batch.GetEvents<T>())
                {
                    var newToken = await DeliverItem(itemTuple.Item1, itemTuple.Item2);
                    if (newToken != null)
                    {
                        return newToken;
                    }
                }
                return default(StreamSequenceToken);
            }

            internal int GetObserverCountForStream(StreamId streamId)
            {
                return localObserver != null && localObserver.StreamId.Equals(streamId) ? 1 : 0;
            }

            internal StreamSubscriptionHandleImpl<T> GetLocalObserver()
            {
                return localObserver;
            }

            public Task CompleteStream()
            {
                return (localObserver == null)
                    ? TaskDone.Done
                    : localObserver.OnCompletedAsync();
            }

            public Task ErrorInStream(Exception exc)
            {
                return (localObserver == null)
                    ? TaskDone.Done
                    : localObserver.OnErrorAsync(exc);
            }
        }

        private readonly IStreamProviderRuntime providerRuntime;
        private readonly ConcurrentDictionary<GuidId, IStreamObservers> allStreamObservers; // map to different ObserversCollection<T> of different Ts.
        private readonly Logger logger;


        internal StreamConsumerExtension(IStreamProviderRuntime providerRt)
        {
            providerRuntime = providerRt;
            allStreamObservers = new ConcurrentDictionary<GuidId, IStreamObservers>();
            logger = providerRuntime.GetLogger(this.GetType().Name);
        }

        internal StreamSubscriptionHandleImpl<T> SetObserver<T>(GuidId subscriptionId, StreamImpl<T> stream, IAsyncObserver<T> observer, StreamSequenceToken token, IStreamFilterPredicateWrapper filter)
        {
            if (null == stream) throw new ArgumentNullException("stream");
            if (null == observer) throw new ArgumentNullException("observer");

            try
            {
                if (logger.IsVerbose) logger.Verbose("{0} AddObserver for stream {1}", providerRuntime.ExecutingEntityIdentity(), stream);

                // Note: The caller [StreamConsumer] already handles locking for Add/Remove operations, so we don't need to repeat here.
                var obs = allStreamObservers.GetOrAdd(subscriptionId, new ObserversCollection<T>()) as ObserversCollection<T>;
                var wrapper = new StreamSubscriptionHandleImpl<T>(subscriptionId, observer, stream, filter);
                obs.SetObserver(wrapper, token);
                return wrapper;
            }
            catch (Exception exc)
            {
                logger.Error((int)ErrorCode.StreamProvider_AddObserverException, String.Format("{0} StreamConsumerExtension.AddObserver({1}) caugth exception.", 
                    providerRuntime.ExecutingEntityIdentity(), stream), exc);
                throw;
            }
        }

        internal bool RemoveObserver<T>(StreamSubscriptionHandle<T> handle)
        {
            var observerWrapper = (StreamSubscriptionHandleImpl<T>)handle;
            IStreamObservers obs;
            // Note: The caller [StreamConsumer] already handles locking for Add/Remove operations, so we don't need to repeat here.
            if (!allStreamObservers.TryGetValue(observerWrapper.SubscriptionId, out obs)) return true;

            var observersCollection = (ObserversCollection<T>)obs;
            observersCollection.RemoveObserver();
            observerWrapper.Clear();
            if (!observersCollection.IsEmpty) return false;

            IStreamObservers ignore;
            allStreamObservers.TryRemove(observerWrapper.SubscriptionId, out ignore);
            // if we don't have any more subsribed streams, unsubscribe the extension.
            return true;
        }

        public Task<StreamSequenceToken> DeliverItem(GuidId subscriptionId, Immutable<object> item, StreamSequenceToken token)
        {
            if (logger.IsVerbose3) logger.Verbose3("DeliverItem {0} for subscription {1}", item.Value, subscriptionId);

            IStreamObservers observers;
            if (allStreamObservers.TryGetValue(subscriptionId, out observers))
                return observers.DeliverItem(item.Value, token);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an item for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return Task.FromResult(default(StreamSequenceToken));
        }

        public Task<StreamSequenceToken> DeliverBatch(GuidId subscriptionId, Immutable<IBatchContainer> batch)
        {
            if (logger.IsVerbose3) logger.Verbose3("DeliverBatch {0} for subscription {1}", batch.Value, subscriptionId);

            IStreamObservers observers;

            if (allStreamObservers.TryGetValue(subscriptionId, out observers))
                return observers.DeliverBatch(batch.Value);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForBatch), "{0} got an item for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return Task.FromResult(default(StreamSequenceToken));
        }

        public Task CompleteStream(GuidId subscriptionId)
        {
            if (logger.IsVerbose3) logger.Verbose3("CompleteStream for subscription {0}", subscriptionId);

            IStreamObservers observers;
            if (allStreamObservers.TryGetValue(subscriptionId, out observers))
                return observers.CompleteStream();

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got a Complete for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task ErrorInStream(GuidId subscriptionId, Exception exc)
        {
            if (logger.IsVerbose3) logger.Verbose3("ErrorInStream {0} for subscription {1}", exc, subscriptionId);

            IStreamObservers observers;
            if (allStreamObservers.TryGetValue(subscriptionId, out observers))
                return observers.ErrorInStream(exc);

            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an Error for subscription {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), subscriptionId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task<StreamSequenceToken> GetSequenceToken(GuidId subscriptionId)
        {
            IStreamObservers observers;
            return Task.FromResult(allStreamObservers.TryGetValue(subscriptionId, out observers) ? observers.Token : default(StreamSequenceToken));
        }

        internal int DiagCountStreamObservers<T>(StreamId streamId)
        {
            return allStreamObservers.Values
                                     .OfType<ObserversCollection<T>>()
                                     .Aggregate(0, (count,o) => count + o.GetObserverCountForStream(streamId));
        }

        internal IList<StreamSubscriptionHandleImpl<T>> GetAllStreamHandles<T>()
        {
            return allStreamObservers.Values
                .OfType<ObserversCollection<T>>()
                .Select(o => o.GetLocalObserver())
                .Where(o => o != null)
                .ToList();
        }
    }
}
