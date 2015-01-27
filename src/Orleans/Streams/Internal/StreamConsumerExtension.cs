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

﻿using System;
using System.Collections.Concurrent;
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
            Task DeliverItem(object item, StreamSequenceToken token);
            Task DeliverBatch(IBatchContainer item);
            Task CompleteStream();
            Task ErrorInStream(Exception exc);
        }

        [Serializable]
        private class ObserversCollection<T> : IStreamObservers
        {
            private readonly ConcurrentDictionary<Guid, ObserverWrapper<T>> localObservers;

            internal ObserversCollection()
            {
                localObservers = new ConcurrentDictionary<Guid, ObserverWrapper<T>>();
            }

            internal void AddObserver(ObserverWrapper<T> observer)
            {
                localObservers.TryAdd(observer.ObserverGuid, observer);
            }

            internal void RemoveObserver(ObserverWrapper<T> observer)
            {
                ObserverWrapper<T> ignore;
                localObservers.TryRemove(observer.ObserverGuid, out ignore);
            }

            internal bool IsEmpty
            {
                get { return localObservers.IsEmpty; }
            }

            public async Task DeliverItem(object item, StreamSequenceToken token)
            {
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
                foreach (ObserverWrapper<T> observer in localObservers.Values)
                {
                    // Flow control to not pass in this item if the streamConsumer is still processing another item
                    // (that is, don't ignore the Task, use it for synchronization, keep an internal queue, etc.).
                    await observer.OnNextAsync(typedItem, token);
                }
            }

            public async Task DeliverBatch(IBatchContainer batch)
            {
                foreach (var itemTuple in batch.GetEvents<T>())
                    await DeliverItem(itemTuple.Item1, itemTuple.Item2);
            }

            internal int Count
            {
                get { return localObservers.Count; }
            }

            public async Task CompleteStream()
            {
                foreach (ObserverWrapper<T> observer in localObservers.Values)
                    await observer.OnCompletedAsync();
            }

            public async Task ErrorInStream(Exception exc)
            {
                foreach (ObserverWrapper<T> observer in localObservers.Values)
                    await observer.OnErrorAsync(exc);
            }
        }

        private readonly IStreamProviderRuntime providerRuntime;
        private readonly ConcurrentDictionary<StreamId, IStreamObservers> allStreamObservers; // map to different ObserversCollection<T> of different Ts.
        private readonly Logger logger;


        internal StreamConsumerExtension(IStreamProviderRuntime providerRt)
        {
            providerRuntime = providerRt;
            allStreamObservers = new ConcurrentDictionary<StreamId, IStreamObservers>();
            logger = providerRuntime.GetLogger(this.GetType().Name);
        }

        internal StreamSubscriptionHandle<T> AddObserver<T>(StreamImpl<T> stream, IAsyncObserver<T> observer, IStreamFilterPredicateWrapper filter)
        {
            if (null == stream) throw new ArgumentNullException("stream");
            if (null == observer) throw new ArgumentNullException("observer");

            try
            {
                if (logger.IsVerbose) logger.Verbose("{0} AddObserver for stream {1}", providerRuntime.ExecutingEntityIdentity(), stream);

                // Note: The caller [StreamConsumer] already handles locking for Add/Remove operations, so we don't need to repeat here.
                IStreamObservers obs = allStreamObservers.GetOrAdd(stream.StreamId, new ObserversCollection<T>());
                var wrapper = new ObserverWrapper<T>(observer, stream, filter);
                ((ObserversCollection<T>)obs).AddObserver(wrapper);
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
            var observerWrapper = (ObserverWrapper<T>)handle;
            IStreamObservers obs;
            // Note: The caller [StreamConsumer] already handles locking for Add/Remove operations, so we don't need to repeat here.
            if (!allStreamObservers.TryGetValue(observerWrapper.StreamId, out obs)) return true;

            var observersCollection = (ObserversCollection<T>)obs;
            observersCollection.RemoveObserver(observerWrapper);
            observerWrapper.Clear();
            if (!observersCollection.IsEmpty) return false;

            IStreamObservers ignore;
            allStreamObservers.TryRemove(observerWrapper.StreamId, out ignore);
            // if we don't have any more subsribed streams, unsubscribe the extension.
            return true;
        }

        public Task DeliverItem(StreamId streamId, Immutable<object> item, StreamSequenceToken token)
        {
            if (logger.IsVerbose3) logger.Verbose3("DeliverItem {0} for stream {1}", item.Value, streamId);

            IStreamObservers observers;
            if (allStreamObservers.TryGetValue(streamId, out observers))
                return observers.DeliverItem(item.Value, token);
           
            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an item for stream {1}, but I don't have any subscriber for that stream. Dropping on the floor.", 
                providerRuntime.ExecutingEntityIdentity(), streamId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task DeliverBatch(StreamId streamId, Immutable<IBatchContainer> batch)
        {
            if (logger.IsVerbose3) logger.Verbose3("DeliverBatch {0} for stream {1}", batch.Value, streamId);

            IStreamObservers observers;
            
            if (allStreamObservers.TryGetValue(streamId, out observers))
                return observers.DeliverBatch(batch.Value);
            
            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForBatch), "{0} got an item for stream {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), streamId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task CompleteStream(StreamId streamId)
        {
            if (logger.IsVerbose3) logger.Verbose3("CompleteStream for stream {0}", streamId);

            IStreamObservers observers;
            if (allStreamObservers.TryGetValue(streamId, out observers))
                return observers.CompleteStream();
            
            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got a Complete for stream {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), streamId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        public Task ErrorInStream(StreamId streamId, Exception exc)
        {
            if (logger.IsVerbose3) logger.Verbose3("ErrorInStream {0} for stream {1}", exc, streamId);

            IStreamObservers observers;
            if (allStreamObservers.TryGetValue(streamId, out observers))
                return observers.ErrorInStream(exc);
            
            logger.Warn((int)(ErrorCode.StreamProvider_NoStreamForItem), "{0} got an Error for stream {1}, but I don't have any subscriber for that stream. Dropping on the floor.",
                providerRuntime.ExecutingEntityIdentity(), streamId);
            // We got an item when we don't think we're the subscriber. This is a normal race condition.
            // We can drop the item on the floor, or pass it to the rendezvous, or ...
            return TaskDone.Done;
        }

        internal int DiagCountStreamObservers<T>(StreamId streamId)
        {
            return ((ObserversCollection<T>) allStreamObservers[streamId]).Count;
        }



        /// <summary>
        /// Wraps a single application observer object, mainly to add Dispose fuctionality.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        [Serializable]
        internal class ObserverWrapper<T> : StreamSubscriptionHandle<T>, IAsyncObserver<T>, IStreamFilterPredicateWrapper
        {
            [NonSerialized]
            private IAsyncObserver<T> observer;
            private readonly StreamImpl<T> streamImpl;
            internal readonly Guid ObserverGuid;
            private readonly IStreamFilterPredicateWrapper filterWrapper;

            internal StreamId StreamId { get { return streamImpl.StreamId; } }
            public object FilterData { get { return filterWrapper != null ? filterWrapper.FilterData : null; } }
            public override IAsyncStream<T> Stream { get { return streamImpl; } }

            public ObserverWrapper(IAsyncObserver<T> observer, StreamImpl<T> stream, IStreamFilterPredicateWrapper filterWrapper)
            {
                this.observer = observer;
                streamImpl = stream;
                ObserverGuid = Guid.NewGuid();
                this.filterWrapper = filterWrapper;
            }

            #region IAsyncObserver methods
            public Task OnNextAsync(T item, StreamSequenceToken token)
            {
                // This method could potentially be invoked after Dispose() has been called, 
                // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
                if (observer == null) 
                    return TaskDone.Done;

                if (filterWrapper != null && !filterWrapper.ShouldReceive(streamImpl, FilterData, item))
                    return TaskDone.Done;

                return observer.OnNextAsync(item, token);
            }

            public Task OnCompletedAsync()
            {
                return observer == null ? TaskDone.Done : observer.OnCompletedAsync();
            }

            public Task OnErrorAsync(Exception ex)
            {
                return observer == null ? TaskDone.Done : observer.OnErrorAsync(ex);
            }

            internal void Clear()
            {
                observer = null;
            }
            #endregion

            #region IStreamFilterPredicateWrapper methods
            public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
            {
                return filterWrapper == null || filterWrapper.ShouldReceive(stream, filterData, item);
            }

            #endregion

            #region IEquatable<StreamId> Members

            public override bool Equals(StreamSubscriptionHandle<T> other)
            {
                var o = other as ObserverWrapper<T>;
                return o != null && ObserverGuid == o.ObserverGuid;
            }

            #endregion

            public override bool Equals(object obj)
            {
                return Equals(obj as ObserverWrapper<T>);
            }

            public override int GetHashCode()
            {
                return ObserverGuid.GetHashCode();
            }

            public override string ToString()
            {
                return String.Format("StreamSubscriptionHandle:Stream={0},ObserverId={1}", Stream, ObserverGuid);
            }
        }
    }
}