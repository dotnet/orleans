using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Streams
{
    [Serializable]
    [Immutable]
    internal class StreamImpl<T> : IStreamIdentity, IAsyncStream<T>, IStreamControl, ISerializable, IOnDeserialized
    {
        private readonly StreamId                               streamId;
        private readonly bool                                   isRewindable;
        [NonSerialized]
        private IInternalStreamProvider                         provider;
        [NonSerialized]
        private volatile IInternalAsyncBatchObserver<T>         producerInterface;
        [NonSerialized]
        private IInternalAsyncObservable<T>                     consumerInterface;
        [NonSerialized]
        private readonly object                                 initLock; // need the lock since the same code runs in the provider on the client and in the silo.

        [NonSerialized]
        private IRuntimeClient                                  runtimeClient;
        
        internal StreamId StreamId                              { get { return streamId; } }

        public bool IsRewindable                                { get { return isRewindable; } }
        public Guid Guid                                        { get { return streamId.Guid; } }
        public string Namespace                                 { get { return streamId.Namespace; } }
        public string ProviderName                              { get { return streamId.ProviderName; } }

        // IMPORTANT: This constructor needs to be public for Json deserialization to work.
        public StreamImpl()
        {
            initLock = new object();
        }

        internal StreamImpl(StreamId streamId, IInternalStreamProvider provider, bool isRewindable, IRuntimeClient runtimeClient)
        {
            this.streamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
            this.runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
            producerInterface = null;
            consumerInterface = null;
            initLock = new object();
            this.isRewindable = isRewindable;
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return GetConsumerInterface().SubscribeAsync(observer, null);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer, StreamSequenceToken token,
            StreamFilterPredicate filterFunc = null,
            object filterData = null)
        {
            return GetConsumerInterface().SubscribeAsync(observer, token, filterFunc, filterData);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> batchObserver)
        {
            return GetConsumerInterface().SubscribeAsync(batchObserver);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> batchObserver, StreamSequenceToken token)
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

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
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
            var observer = producerInterface as IAsyncObserver<T>;
            var batchObserver = producerInterface as IAsyncBatchObserver<T>;
            return Task.WhenAll(observer.OnCompletedAsync(), batchObserver.OnCompletedAsync());
        }

        public Task OnErrorAsync(Exception ex)
        {
            IInternalAsyncBatchObserver<T> producerInterface = GetProducerInterface();
            var observer = producerInterface as IAsyncObserver<T>;
            var batchObserver = producerInterface as IAsyncBatchObserver<T>;
            return Task.WhenAll(observer.OnErrorAsync(ex), batchObserver.OnErrorAsync(ex));
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
                
                producerInterface = provider.GetProducerInterface(this);
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
                        
                        consumerInterface = provider.GetConsumerInterface<T>(this);
                    }
                }
            }
            return consumerInterface;
        }

        private IInternalStreamProvider GetStreamProvider()
        {
            return this.runtimeClient.ServiceProvider.GetRequiredServiceByName<IStreamProvider>(streamId.ProviderName) as IInternalStreamProvider;
        }

        public int CompareTo(IAsyncStream<T> other)
        {
            var o = other as StreamImpl<T>;
            return o == null ? 1 : streamId.CompareTo(o.streamId);
        }

        public virtual bool Equals(IAsyncStream<T> other)
        {
            var o = other as StreamImpl<T>;
            return o != null && streamId.Equals(o.streamId);
        }

        public override bool Equals(object obj)
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

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Use the AddValue method to specify serialized values.
            info.AddValue("StreamId", streamId, typeof(StreamId));
            info.AddValue("IsRewindable", isRewindable, typeof(bool));
        }

        // The special constructor is used to deserialize values. 
        protected StreamImpl(SerializationInfo info, StreamingContext context)
        {
            // Reset the property value using the GetValue method.
            streamId = (StreamId)info.GetValue("StreamId", typeof(StreamId));
            isRewindable = info.GetBoolean("IsRewindable");
            initLock = new object();

            var serializerContext = context.Context as ISerializerContext;
            ((IOnDeserialized)this).OnDeserialized(serializerContext);

        }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.runtimeClient = context?.AdditionalContext as IRuntimeClient;
        }
    }
}
