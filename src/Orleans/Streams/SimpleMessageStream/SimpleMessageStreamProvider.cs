using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    public class SimpleMessageStreamProvider : IInternalStreamProvider
    {
        public string                       Name { get; private set; }

        private Logger                      logger;
        private IStreamProviderRuntime      providerRuntime;
        private bool                        fireAndForgetDelivery;
        internal const string               FIRE_AND_FORGET_DELIVERY = "FireAndForgetDelivery";
        internal const StreamPubSubType     DEFAULT_STREAM_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;

        public bool IsRewindable { get { return false; } }

        public Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            this.Name = name;
            providerRuntime = (IStreamProviderRuntime) providerUtilitiesManager;
            string fireAndForgetDeliveryStr;
            fireAndForgetDelivery = config.Properties.TryGetValue(FIRE_AND_FORGET_DELIVERY, out fireAndForgetDeliveryStr) && Boolean.Parse(fireAndForgetDeliveryStr);

            logger = providerRuntime.GetLogger(this.GetType().Name);
            logger.Info("Initialized SimpleMessageStreamProvider with name {0} and with property FireAndForgetDelivery: {1}.", Name, fireAndForgetDelivery);
            return TaskDone.Done;
        }

        public Task Start()
        {
            return TaskDone.Done;
        }

        public Task Close()
        {
            return TaskDone.Done;
        }

        public IAsyncStream<T> GetStream<T>(Guid id, string streamNamespace)
        {
            var streamId = StreamId.GetStreamId(id, Name, streamNamespace);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                streamId,
                () => new StreamImpl<T>(streamId, this, IsRewindable));
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> stream)
        {
            return new SimpleMessageStreamProducer<T>((StreamImpl<T>)stream, Name, providerRuntime, fireAndForgetDelivery, IsRewindable);
        }

        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            return GetConsumerInterfaceImpl(streamId);
        }

        private IInternalAsyncObservable<T> GetConsumerInterfaceImpl<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime, providerRuntime.PubSub(DEFAULT_STREAM_PUBSUB_TYPE), IsRewindable);
        }
    }
}
