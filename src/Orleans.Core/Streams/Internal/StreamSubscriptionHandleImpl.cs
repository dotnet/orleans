using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal class StreamSubscriptionHandleImpl<T> : StreamSubscriptionHandle<T>, IStreamSubscriptionHandle 
    {
        private StreamImpl<T> streamImpl;
        private readonly IStreamFilterPredicateWrapper filterWrapper;
        private readonly GuidId subscriptionId;
        private readonly bool isRewindable;

        [NonSerialized]
        private IAsyncObserver<T> observer;
        [NonSerialized]
        private StreamHandshakeToken expectedToken;
        internal bool IsValid { get { return streamImpl != null; } }
        internal GuidId SubscriptionId { get { return subscriptionId; } }
        internal bool IsRewindable { get { return isRewindable; } }

        public override string ProviderName { get { return this.streamImpl.ProviderName; } }
        public override IStreamIdentity StreamIdentity { get { return streamImpl; } }
        public override Guid HandleId { get { return subscriptionId.Guid; } }

        public StreamSubscriptionHandleImpl(GuidId subscriptionId, StreamImpl<T> streamImpl)
            : this(subscriptionId, null, streamImpl, null, null)
        {
        }

        public StreamSubscriptionHandleImpl(GuidId subscriptionId, IAsyncObserver<T> observer, StreamImpl<T> streamImpl, IStreamFilterPredicateWrapper filterWrapper, StreamSequenceToken token)
        {
            if (subscriptionId == null) throw new ArgumentNullException("subscriptionId");
            if (streamImpl == null) throw new ArgumentNullException("streamImpl");

            this.subscriptionId = subscriptionId;
            this.observer = observer;
            this.streamImpl = streamImpl;
            this.filterWrapper = filterWrapper;
            this.isRewindable = streamImpl.IsRewindable;
            if (IsRewindable)
            {
                expectedToken = StreamHandshakeToken.CreateStartToken(token);
            }
        }

        public void Invalidate()
        {
            streamImpl = null;
            observer = null;
        }

        public StreamHandshakeToken GetSequenceToken()
        {
            return expectedToken;
        }

        public override Task UnsubscribeAsync()
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid. It has been used to unsubscribe or resume.");
            return streamImpl.UnsubscribeAsync(this);
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> obs, StreamSequenceToken token = null)
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid. It has been used to unsubscribe or resume.");
            return streamImpl.ResumeAsync(this, obs, token);
        }

        public async Task<StreamHandshakeToken> DeliverBatch(IBatchContainer batch, StreamHandshakeToken handshakeToken)
        {
            // we validate expectedToken only for ordered (rewindable) streams
            if (expectedToken != null)
            {
                if (!expectedToken.Equals(handshakeToken))
                    return expectedToken;
            }

            if (batch is IBatchContainerBatch)
            {
                var batchContainerBatch = batch as IBatchContainerBatch;
                await NextBatch(batchContainerBatch);
            }
            else
            {
                foreach (var itemTuple in batch.GetEvents<T>())
                {
                    await NextItem(itemTuple.Item1, itemTuple.Item2);
                }
            }

            if (IsRewindable)
            {
                expectedToken = StreamHandshakeToken.CreateDeliveyToken(batch.SequenceToken);
            }
            return null;
        }

        public async Task<StreamHandshakeToken> DeliverItem(object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken)
        {
            if (expectedToken != null)
            {
                if (!expectedToken.Equals(handshakeToken))
                    return expectedToken;
            }

            await NextItem(item, currentToken);

            // check again, in case the expectedToken was changed indiretly via ResumeAsync()
            if (expectedToken != null)
            {
                if (!expectedToken.Equals(handshakeToken))
                    return expectedToken;
            }
            if (IsRewindable)
            {
                expectedToken = StreamHandshakeToken.CreateDeliveyToken(currentToken);
            }
            return null;
        }

        public async Task NextBatch(IBatchContainerBatch batchContainerBatch)
        {
            bool isRequestContextSet;
            foreach (var batchContainer in batchContainerBatch.BatchContainers)
            {
                isRequestContextSet = batchContainer.ImportRequestContext();
                foreach (var itemTuple in batchContainer.GetEvents<T>())
                {
                    await NextItem(itemTuple.Item1, itemTuple.Item2);
                }

                if (isRequestContextSet)
                {
                    RequestContext.Clear();
                }
            }
        }

        private Task NextItem(object item, StreamSequenceToken token)
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

            // This method could potentially be invoked after Dispose() has been called, 
            // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
            if (observer == null || !IsValid)
                return Task.CompletedTask;

            if (filterWrapper != null && !filterWrapper.ShouldReceive(streamImpl, filterWrapper.FilterData, typedItem))
                return Task.CompletedTask;

            return observer.OnNextAsync(typedItem, token);
        }

        public Task CompleteStream()
        {
            return observer == null ? Task.CompletedTask : observer.OnCompletedAsync();
        }

        public Task ErrorInStream(Exception ex)
        {
            return observer == null ? Task.CompletedTask : observer.OnErrorAsync(ex);
        }

        internal bool SameStreamId(StreamId streamId)
        {
            return IsValid && streamImpl.StreamId.Equals(streamId);
        }

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            var o = other as StreamSubscriptionHandleImpl<T>;
            return o != null && SubscriptionId.Equals(o.SubscriptionId);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as StreamSubscriptionHandle<T>);
        }

        public override int GetHashCode()
        {
            return SubscriptionId.GetHashCode();
        }

        public override string ToString()
        {
            return String.Format("StreamSubscriptionHandleImpl:Stream={0},HandleId={1}", IsValid ? streamImpl.StreamId.ToString() : "null", HandleId);
        }
    }
}
