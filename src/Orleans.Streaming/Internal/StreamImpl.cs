#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Streams
{
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    [SerializationCallbacks(typeof(OnDeserializedCallbacks))]
    internal sealed class StreamImpl<T> : IAsyncStream<T>, IStreamControl, IOnDeserialized
    {
        [Id(0)]
        private readonly QualifiedStreamId                        streamId;

        [Id(1)]
        private readonly bool                                    isRewindable;

        [NonSerialized]
        private IInternalStreamProvider?                         provider;

        [NonSerialized]
        private volatile IInternalAsyncBatchObserver<T>?         producerInterface;

        [NonSerialized]
        private volatile IInternalAsyncObservable<T>?            consumerInterface;

        [NonSerialized]
        private readonly object initLock = new object();

        [NonSerialized]
        private IRuntimeClient?                                  runtimeClient;

        internal QualifiedStreamId InternalStreamId { get { return streamId; } }
        public StreamId StreamId => streamId;

        public bool IsRewindable => isRewindable;
        public string ProviderName => streamId.ProviderName;

        // Constructor for Orleans serialization, otherwise initLock is null
        public StreamImpl()
        {
        }

        public StreamImpl(QualifiedStreamId streamId, IInternalStreamProvider provider, bool isRewindable, IRuntimeClient runtimeClient)
        {
            this.streamId = streamId;
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
            this.runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
            producerInterface = null;
            consumerInterface = null;
            this.isRewindable = isRewindable;
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return GetConsumerInterface().SubscribeAsync(observer, null);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer, StreamSequenceToken? token, string? filterData = null)
        {
            return GetConsumerInterface().SubscribeAsync(observer, token, filterData);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> batchObserver)
        {
            return GetConsumerInterface().SubscribeAsync(batchObserver);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> batchObserver, StreamSequenceToken? token)
        {
            return GetConsumerInterface().SubscribeAsync(batchObserver, token);
        }

        public async Task Cleanup(bool cleanupProducers, bool cleanupConsumers)
        {
            // Cleanup producers
            if (cleanupProducers && producerInterface != null)
            {
                await producerInterface.Cleanup();
                producerInterface = null;
            }

            // Cleanup consumers
            if (cleanupConsumers && consumerInterface != null)
            {
                await consumerInterface.Cleanup();
                consumerInterface = null;
            }
        }

        public Task OnNextAsync(T item, StreamSequenceToken? token = null)
        {
            return GetProducerInterface().OnNextAsync(item, token);
        }

        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token)
        {
            return GetProducerInterface().OnNextBatchAsync(batch, token);
        }

        public Task OnCompletedAsync()
        {
            IInternalAsyncBatchObserver<T> producerInterface = GetProducerInterface();
            return producerInterface.OnCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            IInternalAsyncBatchObserver<T> producerInterface = GetProducerInterface();
            return producerInterface.OnErrorAsync(ex);
        }

        internal Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            StreamSequenceToken token)
        {
            return GetConsumerInterface().ResumeAsync(handle, observer, token);
        }

        internal Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncBatchObserver<T> observer,
            StreamSequenceToken token)
        {
            return GetConsumerInterface().ResumeAsync(handle, observer, token);
        }

        public Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptionHandles()
        {
            return GetConsumerInterface().GetAllSubscriptions();
        }

        internal Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle)
        {
            return GetConsumerInterface().UnsubscribeAsync(handle);
        }

        internal IInternalAsyncBatchObserver<T> GetProducerInterface()
        {
            if (producerInterface != null) return producerInterface;

            lock (initLock)
            {
                if (producerInterface != null) 
                    return producerInterface;

                if (provider == null)
                    provider = GetStreamProvider();
                
                producerInterface = provider!.GetProducerInterface(this);
            }
            return producerInterface;
        }

        internal IInternalAsyncObservable<T> GetConsumerInterface()
        {
            if (consumerInterface == null)
            {
                lock (initLock)
                {
                    if (consumerInterface == null)
                    {
                        if (provider == null)
                            provider = GetStreamProvider();
                        
                        consumerInterface = provider!.GetConsumerInterface(this);
                    }
                }
            }
            return consumerInterface;
        }

        private IInternalStreamProvider? GetStreamProvider()
        {
            return this.runtimeClient?.ServiceProvider.GetRequiredServiceByName<IStreamProvider>(streamId.ProviderName) as IInternalStreamProvider;
        }

        public int CompareTo(IAsyncStream<T>? other)
        {
            var o = other as StreamImpl<T>;
            return o == null ? 1 : streamId.CompareTo(o.streamId);
        }

        public bool Equals(IAsyncStream<T>? other)
        {
            var o = other as StreamImpl<T>;
            return o != null && streamId.Equals(o.streamId);
        }

        public override bool Equals(object? obj)
        {
            var o = obj as StreamImpl<T>;
            return o != null && streamId.Equals(o.streamId);
        }

        public override int GetHashCode()
        {
            return streamId.GetHashCode();
        }

        public override string ToString()
        {
            return streamId.ToString();
        }

        void IOnDeserialized.OnDeserialized(DeserializationContext  context)
        {
            this.runtimeClient = context?.RuntimeClient as IRuntimeClient;
        }
    }
}
