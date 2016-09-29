using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    [Immutable]
    internal class StreamImpl<T> : IStreamIdentity, IAsyncStream<T>, IStreamControl, ISerializable
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

        internal StreamImpl(StreamId streamId, IInternalStreamProvider provider, bool isRewindable)
        {
            if (null == streamId)
                throw new ArgumentNullException("streamId");
            if (null == provider)
                throw new ArgumentNullException("provider");

            this.streamId = streamId;
            this.provider = provider;
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

        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token = null)
        {
            return GetProducerInterface().OnNextBatchAsync(batch, token);
        }
        public Task OnCompletedAsync()
        {
            return GetProducerInterface().OnCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            return GetProducerInterface().OnErrorAsync(ex);
        }

        internal Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
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

        internal IAsyncBatchObserver<T> GetProducerInterface()
        {
            if (producerInterface != null) return producerInterface;

            lock (initLock)
            {
                if (producerInterface != null) 
                    return producerInterface;

                if (provider == null)
                    provider = GetStreamProvider();
                
                producerInterface = provider.GetProducerInterface<T>(this);
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
            return RuntimeClient.Current.CurrentStreamProviderManager.GetProvider(streamId.ProviderName) as IInternalStreamProvider;
        }

        #region IComparable<IAsyncStream<T>> Members

        public int CompareTo(IAsyncStream<T> other)
        {
            var o = other as StreamImpl<T>;
            return o == null ? 1 : streamId.CompareTo(o.streamId);
        }

        #endregion

        #region IEquatable<IAsyncStream<T>> Members

        public virtual bool Equals(IAsyncStream<T> other)
        {
            var o = other as StreamImpl<T>;
            return o != null && streamId.Equals(o.streamId);
        }

        #endregion

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
                
        #region ISerializable Members

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
        }

        #endregion
    }
}
