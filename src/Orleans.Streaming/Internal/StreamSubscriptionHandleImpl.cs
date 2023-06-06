using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    [GenerateSerializer]
    internal class StreamSubscriptionHandleImpl<T> : StreamSubscriptionHandle<T>, IStreamSubscriptionHandle 
    {
        [Id(0)]
        [JsonProperty]
        private StreamImpl<T> streamImpl;
        [Id(1)]
        [JsonProperty]
        private readonly string filterData;
        [Id(2)]
        [JsonProperty]
        private readonly GuidId subscriptionId;
        [Id(3)]
        private readonly bool isRewindable;

        [NonSerialized]
        private IAsyncObserver<T> observer;
        [NonSerialized]
        private IAsyncBatchObserver<T> batchObserver;
        [NonSerialized]
        private StreamHandshakeToken expectedToken;
        internal bool IsValid => streamImpl != null;
        internal GuidId SubscriptionId => subscriptionId;
        internal bool IsRewindable => isRewindable;

        public override string ProviderName => streamImpl.ProviderName;
        public override StreamId StreamId => streamImpl.StreamId;
        public override Guid HandleId => subscriptionId.Guid;


        [JsonConstructor]
        public StreamSubscriptionHandleImpl(GuidId subscriptionId, StreamImpl<T> streamImpl, string filterData)
            : this(subscriptionId, null, null, streamImpl, null, filterData)
        {
        }

        public StreamSubscriptionHandleImpl(GuidId subscriptionId, StreamImpl<T> streamImpl)
            : this(subscriptionId, null, null, streamImpl, null, null)
        {
        }

        public StreamSubscriptionHandleImpl(
            GuidId subscriptionId,
            IAsyncObserver<T> observer,
            IAsyncBatchObserver<T> batchObserver,
            StreamImpl<T> streamImpl,
            StreamSequenceToken token,
            string filterData)
        {
            this.subscriptionId = subscriptionId ?? throw new ArgumentNullException("subscriptionId");
            this.observer = observer;
            this.batchObserver = batchObserver;
            this.streamImpl = streamImpl ?? throw new ArgumentNullException("streamImpl");
            this.filterData = filterData;
            isRewindable = streamImpl.IsRewindable;
            if (IsRewindable)
            {
                expectedToken = StreamHandshakeToken.CreateStartToken(token);
            }
        }

        public void Invalidate()
        {
            streamImpl = null;
            observer = null;
            batchObserver = null;
        }

        public StreamHandshakeToken GetSequenceToken() => expectedToken;

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


        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncBatchObserver<T> observer, StreamSequenceToken token = null)
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid. It has been used to unsubscribe or resume.");
            return streamImpl.ResumeAsync(this, observer, token);
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
                if (observer != null)
                {
                    foreach (var itemTuple in batch.GetEvents<T>())
                    {
                        await NextItem(itemTuple.Item1, itemTuple.Item2);
                    }
                } else
                {
                    await NextItems(batch.GetEvents<T>());
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

            await ((observer != null)
                ? NextItem(typedItem, currentToken)
                : NextItems(new[] { Tuple.Create(typedItem, currentToken) }));

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
            if (observer != null)
            {
                foreach (var batchContainer in batchContainerBatch.BatchContainers)
                {
                    var isRequestContextSet = batchContainer.ImportRequestContext();
                    try
                    {
                        foreach (var itemTuple in batchContainer.GetEvents<T>())
                        {
                            await NextItem(itemTuple.Item1, itemTuple.Item2);
                        }
                    }
                    finally
                    {
                        if (isRequestContextSet)
                        {
                            RequestContext.Clear();
                        }
                    }
                }
            }
            else
            {
                await NextItems(batchContainerBatch.BatchContainers.SelectMany(batch => batch.GetEvents<T>()));
            }
        }

        private Task NextItem(T item, StreamSequenceToken token)
        {
            // This method could potentially be invoked after Dispose() has been called, 
            // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
            if (observer == null || !IsValid)
                return Task.CompletedTask;

            return observer.OnNextAsync(item, token);
        }

        private Task NextItems(IEnumerable<Tuple<T, StreamSequenceToken>> items)
        {
            // This method could potentially be invoked after Dispose() has been called, 
            // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
            if (batchObserver == null || !IsValid)
                return Task.CompletedTask;

            IList<SequentialItem<T>> batch = items
                .Select(item => new SequentialItem<T>(item.Item1, item.Item2))
                .ToList();

            return batch.Count != 0 ? batchObserver.OnNextAsync(batch) : Task.CompletedTask;
        }

        public Task CompleteStream()
        {
            return observer is null
                ? batchObserver is null
                    ? Task.CompletedTask
                    : batchObserver.OnCompletedAsync()
                : observer.OnCompletedAsync();
        }

        public Task ErrorInStream(Exception ex)
        {
            return observer is null
                ? batchObserver is null
                    ? Task.CompletedTask
                    : batchObserver.OnErrorAsync(ex)
                : observer.OnErrorAsync(ex);
        }

        internal bool SameStreamId(QualifiedStreamId streamId) => IsValid && streamImpl.InternalStreamId.Equals(streamId);

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            var o = other as StreamSubscriptionHandleImpl<T>;
            return o != null && SubscriptionId.Equals(o.SubscriptionId);
        }

        public override bool Equals(object obj) => Equals(obj as StreamSubscriptionHandle<T>);

        public override int GetHashCode() => SubscriptionId.GetHashCode();

        public override string ToString() => string.Format("StreamSubscriptionHandleImpl:Stream={0},HandleId={1}", IsValid ? streamImpl.InternalStreamId.ToString() : "null", HandleId);
    }
}
