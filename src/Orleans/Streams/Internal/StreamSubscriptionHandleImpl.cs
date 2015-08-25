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
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal class StreamSubscriptionHandleImpl<T> : StreamSubscriptionHandle<T>, IStreamFilterPredicateWrapper, IStreamSubscriptionHandle
    {
        [NonSerialized]
        private IAsyncObserver<T> observer;
        private readonly StreamImpl<T> streamImpl;
        private readonly IStreamFilterPredicateWrapper filterWrapper;

        internal StreamId StreamId { get { return streamImpl.StreamId; } }
        public object FilterData { get { return filterWrapper != null ? filterWrapper.FilterData : null; } }
        public override IStreamIdentity StreamIdentity { get { return streamImpl; } }

        public GuidId SubscriptionId { get; protected set; }
        public bool IsValid { get; private set; }

        public override Guid HandleId { get { return SubscriptionId.Guid; } }

        [NonSerialized]
        private bool dirty;
        [NonSerialized]
        private StreamSequenceToken expectedToken;

        public StreamSubscriptionHandleImpl(GuidId subscriptionId, StreamImpl<T> stream)
            : this(subscriptionId, null, stream, null, null)
        {
        }

        public StreamSubscriptionHandleImpl(GuidId subscriptionId, IAsyncObserver<T> observer, StreamImpl<T> stream, IStreamFilterPredicateWrapper filterWrapper, StreamSequenceToken token)
        {
            if (subscriptionId == null) throw new ArgumentNullException("subscriptionId");
            if (stream == null) throw new ArgumentNullException("stream");

            IsValid = true;
            this.observer = observer;
            streamImpl = stream;
            this.SubscriptionId = subscriptionId;
            this.filterWrapper = filterWrapper;
            expectedToken = token;
            dirty = true;
        }

        public void Invalidate()
        {
            IsValid = false;
        }

        public override Task UnsubscribeAsync()
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid.  It has been used to unsubscribe or resume.");
            return streamImpl.UnsubscribeAsync(this);
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> obs, StreamSequenceToken token = null)
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid.  It has been used to unsubscribe or resume.");
            return streamImpl.ResumeAsync(this, obs, token);
        }

        public StreamSequenceToken Token
        {
            get { return expectedToken; }
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

            await NextItem(typedItem, token);

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


        private Task NextItem(T item, StreamSequenceToken token)
        {
            // This method could potentially be invoked after Dispose() has been called, 
            // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
            if (observer == null)
                return TaskDone.Done;

            if (filterWrapper != null && !filterWrapper.ShouldReceive(streamImpl, FilterData, item))
                return TaskDone.Done;

            return observer.OnNextAsync(item, token);
        }

        public Task CompleteStream()
        {
            return observer == null ? TaskDone.Done : observer.OnCompletedAsync();
        }

        public Task ErrorInStream(Exception ex)
        {
            return observer == null ? TaskDone.Done : observer.OnErrorAsync(ex);
        }

        internal void Clear()
        {
            observer = null;
            dirty = false;
        }

        #region IStreamFilterPredicateWrapper methods
        public bool ShouldReceive(IStreamIdentity stream, object filterData, object item)
        {
            return filterWrapper == null || filterWrapper.ShouldReceive(stream, filterData, item);
        }

        #endregion

        #region IEquatable<StreamId> Members

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            var o = other as StreamSubscriptionHandleImpl<T>;
            return o != null && SubscriptionId.Equals(o.SubscriptionId);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as StreamSubscriptionHandle<T>);
        }

        #endregion
        
        public override int GetHashCode()
        {
            return SubscriptionId.GetHashCode();
        }

        public override string ToString()
        {
            return String.Format("StreamSubscriptionHandleImpl:Stream={0},Subscription={1}", StreamIdentity, SubscriptionId);
        }
    }
}
