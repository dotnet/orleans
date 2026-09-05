using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;

namespace Orleans.Streams
{
    [Serializable]
    [GenerateSerializer]
    internal class StreamSubscriptionHandleImpl<T> : StreamSubscriptionHandle<T>, IStreamSubscriptionHandle
    {
        [Id(0)]
        [JsonProperty]
        private StreamImpl<T>? streamImpl;
        [Id(1)]
        [JsonProperty]
        private readonly string? filterData;
        [Id(2)]
        [JsonProperty]
        private readonly GuidId subscriptionId;
        [Id(3)]
        private readonly bool isRewindable;

        [NonSerialized]
        private IAsyncObserver<T>? observer;
        [NonSerialized]
        private IAsyncBatchObserver<T>? batchObserver;
        [NonSerialized]
        private SharedHandshakeState? handshakeState;
        [NonSerialized]
        private readonly SiloAddress? siloAddress;
        [NonSerialized]
        private readonly string? clusterId;
        private StreamHandshakeToken? expectedToken
        {
            get => SharedHandshake.Token;
            set => SharedHandshake.Token = value;
        }

        internal bool IsValid { get { return streamImpl != null; } }
        internal GuidId SubscriptionId { get { return subscriptionId; } }
        internal bool IsRewindable { get { return isRewindable; } }
        internal string? FilterData { get { return filterData; } }
        internal bool HasObserver { get { return observer is not null || batchObserver is not null; } }
        internal SharedHandshakeState SharedHandshake => handshakeState ??= new();

        public override string ProviderName { get { return this.streamImpl!.ProviderName; } }
        public override StreamId StreamId { get { return streamImpl!.StreamId; } }
        public override Guid HandleId { get { return subscriptionId.Guid; } }


        [JsonConstructor]
        public StreamSubscriptionHandleImpl(GuidId subscriptionId, StreamImpl<T> streamImpl, string? filterData)
            : this(subscriptionId, null, null, streamImpl, null, startPosition: null, filterData)
        {
        }

        public StreamSubscriptionHandleImpl(GuidId subscriptionId, StreamImpl<T> streamImpl)
            : this(subscriptionId, null, null, streamImpl, null, startPosition: null, filterData: null)
        {
        }

        public StreamSubscriptionHandleImpl(
            GuidId subscriptionId,
            IAsyncObserver<T>? observer,
            IAsyncBatchObserver<T>? batchObserver,
            StreamImpl<T> streamImpl,
            StreamSequenceToken? token,
            StreamSubscriptionStartPosition? startPosition,
            string? filterData,
            bool disableHandshake = false,
            SharedHandshakeState? handshakeState = null,
            SiloAddress? siloAddress = null,
            string? clusterId = null)
        {
            this.subscriptionId = subscriptionId ?? throw new ArgumentNullException(nameof(subscriptionId));
            this.observer = observer;
            this.batchObserver = batchObserver;
            this.streamImpl = streamImpl ?? throw new ArgumentNullException(nameof(streamImpl));
            this.filterData = filterData;
            this.handshakeState = handshakeState;
            this.siloAddress = siloAddress;
            this.clusterId = clusterId;
            this.isRewindable = streamImpl.IsRewindable && !disableHandshake;
            startPosition?.Validate();
            if (token is not null && IsRewindable)
            {
                expectedToken ??= StreamHandshakeToken.CreateStartToken(token);
            }
            else if (startPosition is { } position)
            {
                expectedToken ??= StreamHandshakeToken.CreateStartPositionToken(position);
            }
        }

        public void Invalidate()
        {
            this.streamImpl = null;
            this.observer = null;
            this.batchObserver = null;
        }

        public StreamHandshakeToken? GetSequenceToken()
        {
            return expectedToken;
        }

        internal void ValidateResumeToken(StreamSequenceToken? token)
            => ValidateResumeToken(subscriptionId, HasObserver, expectedToken, token);

        internal static void ValidateResumeToken(
            GuidId subscriptionId,
            bool hasObserver,
            StreamHandshakeToken? expectedToken,
            StreamSequenceToken? token)
        {
            // A DeliveryToken is recorded only after the complete delivery call succeeds.
            if (token is null
                || !hasObserver
                || !SubscriptionMarker.IsImplicitSubscription(subscriptionId.Guid)
                || expectedToken is not DeliveryToken deliveryToken)
            {
                return;
            }

            throw new InvalidOperationException(
                $"An active implicit stream subscription cannot resume from token {token} because it has already completed delivery through token {deliveryToken.Token}. "
                + "Implicit subscriptions advance monotonically within an activation.");
        }

        internal sealed class SharedHandshakeState
        {
            public StreamHandshakeToken? Token { get; set; }
        }

        public override Task UnsubscribeAsync()
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid. It has been used to unsubscribe or resume.");
            return this.streamImpl!.UnsubscribeAsync(this);
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> obs, StreamSequenceToken? token = null)
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid. It has been used to unsubscribe or resume.");
            return this.streamImpl!.ResumeAsync(this, obs, token);
        }


        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncBatchObserver<T> observer, StreamSequenceToken? token = null)
        {
            if (!IsValid) throw new InvalidOperationException("Handle is no longer valid. It has been used to unsubscribe or resume.");
            return this.streamImpl!.ResumeAsync(this, observer, token);
        }

        public async Task<StreamHandshakeToken?> DeliverBatch(IBatchContainer batch, StreamHandshakeToken? handshakeToken)
        {
            // we validate expectedToken only for ordered (rewindable) streams
            if (this.expectedToken != null)
            {
                if (!this.expectedToken.Equals(handshakeToken))
                    return this.expectedToken;

                // Check if this even already has been delivered
                if (IsRewindable)
                {
                    if (this.expectedToken is DeliveryToken deliveryToken
                        && deliveryToken.Token is { } deliveredToken
                        && EventSequenceTokenCompatibility.AreEqual(deliveredToken, batch.SequenceToken))
                        return this.expectedToken;
                }
            }

            var currentStream = this.streamImpl;
            var streamProviderName = currentStream?.ProviderName;
            var streamId = currentStream?.StreamId ?? default;
            var currentSubscriptionId = subscriptionId.Guid;

            if (batch is IBatchContainerBatch)
            {
                var batchContainerBatch = batch as IBatchContainerBatch;
                await NextBatch(batchContainerBatch!, streamProviderName, streamId, currentSubscriptionId);
            }
            else
            {
                if (this.observer != null)
                {
                    foreach (var itemTuple in batch.GetEvents<T>())
                    {
                        if (await NextItem(itemTuple.Item1, itemTuple.Item2))
                        {
                            EmitItemDelivered(streamProviderName, streamId, currentSubscriptionId, itemTuple.Item2);
                        }
                    }
                }
                else
                {
                    await NextItems(batch.GetEvents<T>(), streamProviderName, streamId, currentSubscriptionId);
                }
            }

            if (IsRewindable)
            {
                this.expectedToken = StreamHandshakeToken.CreateDeliveyToken(batch.SequenceToken);
            }
            else if (this.expectedToken is StartPositionToken)
            {
                this.expectedToken = null;
            }

            return null;
        }

        public async Task<StreamHandshakeToken?> DeliverItem(object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
        {
            if (this.expectedToken != null)
            {
                if (!this.expectedToken.Equals(handshakeToken))
                    return this.expectedToken;

                // Check if this even already has been delivered
                if (IsRewindable)
                {
                    if (this.expectedToken is DeliveryToken deliveryToken
                        && deliveryToken.Token is { } deliveredToken
                        && EventSequenceTokenCompatibility.AreEqual(deliveredToken, currentToken))
                        return this.expectedToken;
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

            var currentStream = this.streamImpl;
            var streamProviderName = currentStream?.ProviderName;
            var streamId = currentStream?.StreamId ?? default;
            var currentSubscriptionId = subscriptionId.Guid;

            if (this.observer != null)
            {
                if (await NextItem(typedItem, currentToken))
                {
                    EmitItemDelivered(streamProviderName, streamId, currentSubscriptionId, currentToken);
                }
            }
            else
            {
                await NextItems(new[] { Tuple.Create(typedItem, currentToken) }, streamProviderName, streamId, currentSubscriptionId);
            }

            // check again, in case the expectedToken was changed indiretly via ResumeAsync()
            if (this.expectedToken != null)
            {
                if (!this.expectedToken.Equals(handshakeToken))
                    return this.expectedToken;
            }
            if (IsRewindable)
            {
                this.expectedToken = StreamHandshakeToken.CreateDeliveyToken(currentToken);
            }
            else if (this.expectedToken is StartPositionToken)
            {
                this.expectedToken = null;
            }
            return null;
        }

        public async Task NextBatch(IBatchContainerBatch batchContainerBatch, string? streamProviderName, StreamId streamId, Guid currentSubscriptionId)
        {
            if (this.observer != null)
            {
                foreach (var batchContainer in batchContainerBatch.BatchContainers)
                {
                    bool isRequestContextSet = batchContainer.ImportRequestContext();
                    try
                    {
                        foreach (var itemTuple in batchContainer.GetEvents<T>())
                        {
                            if (await NextItem(itemTuple.Item1, itemTuple.Item2))
                            {
                                EmitItemDelivered(streamProviderName, streamId, currentSubscriptionId, itemTuple.Item2);
                            }
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
                await NextItems(batchContainerBatch.BatchContainers.SelectMany(batch => batch.GetEvents<T>()), streamProviderName, streamId, currentSubscriptionId);
            }
        }

        private async Task<bool> NextItem(T item, StreamSequenceToken token)
        {
            // This method could potentially be invoked after Dispose() has been called, 
            // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
            if (this.observer == null || !IsValid)
                return false;

            await this.observer.OnNextAsync(item, token);
            return true;
        }

        private async Task NextItems(IEnumerable<Tuple<T, StreamSequenceToken>> items, string? streamProviderName, StreamId streamId, Guid currentSubscriptionId)
        {
            // This method could potentially be invoked after Dispose() has been called, 
            // so we have to ignore the request or we risk breaking unit tests AQ_01 - AQ_04.
            if (this.batchObserver == null || !IsValid)
                return;

            IList<SequentialItem<T>> batch = items
                .Select(item => new SequentialItem<T>(item.Item1, item.Item2))
                .ToList();

            if (batch.Count == 0)
            {
                return;
            }

            await this.batchObserver.OnNextAsync(batch);
            foreach (var item in batch)
            {
                EmitItemDelivered(streamProviderName, streamId, currentSubscriptionId, item.Token);
            }
        }

        private void EmitItemDelivered(string? streamProviderName, StreamId streamId, Guid currentSubscriptionId, StreamSequenceToken sequenceToken)
        {
            if (streamProviderName is not null)
            {
                StreamingEvents.EmitItemDelivered(
                    streamProviderName,
                    streamId,
                    currentSubscriptionId,
                    siloAddress,
                    clusterId ?? throw new InvalidOperationException("The stream subscription is not bound to a cluster."),
                    sequenceToken);
            }
        }

        public Task CompleteStream()
        {
            return this.observer is null
                ? this.batchObserver is null
                    ? Task.CompletedTask
                    : this.batchObserver.OnCompletedAsync()
                : this.observer.OnCompletedAsync();
        }

        public Task ErrorInStream(Exception ex)
        {
            return this.observer is null
                ? this.batchObserver is null
                    ? Task.CompletedTask
                    : this.batchObserver.OnErrorAsync(ex)
                : this.observer.OnErrorAsync(ex);
        }

        internal bool SameStreamId(QualifiedStreamId streamId)
        {
            return IsValid && streamImpl!.InternalStreamId.Equals(streamId);
        }

        public override bool Equals(StreamSubscriptionHandle<T>? other)
        {
            var o = other as StreamSubscriptionHandleImpl<T>;
            return o != null && SubscriptionId.Equals(o.SubscriptionId);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StreamSubscriptionHandle<T>);
        }

        public override int GetHashCode()
        {
            return SubscriptionId.GetHashCode();
        }

        public override string ToString()
        {
            return string.Format("StreamSubscriptionHandleImpl:Stream={0},HandleId={1}", IsValid ? streamImpl!.InternalStreamId.ToString() : "null", HandleId);
        }
    }
}
