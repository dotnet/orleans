using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    public class SimpleMessageStreamProvider : IInternalStreamProvider, IStreamSubscriptionManagerRetriever
    {
        public string                       Name { get; private set; }

        private Logger                      logger;
        private IStreamProviderRuntime      providerRuntime;
        private bool                        fireAndForgetDelivery;
        private bool                        optimizeForImmutableData;
        private StreamPubSubType            pubSubType;
        private ProviderStateManager        stateManager = new ProviderStateManager();
        private IRuntimeClient              runtimeClient;
        private IStreamSubscriptionManager streamSubscriptionManager;
        internal const string                STREAM_PUBSUB_TYPE = "PubSubType";
        internal const string                FIRE_AND_FORGET_DELIVERY = "FireAndForgetDelivery";
        internal const string                OPTIMIZE_FOR_IMMUTABLE_DATA = "OptimizeForImmutableData";
        internal const StreamPubSubType      DEFAULT_STREAM_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;
        internal const bool DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY = false;
        internal const bool DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA = true;
        public bool IsRewindable { get { return false; } }

        public Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            if (!stateManager.PresetState(ProviderState.Initialized)) return Task.CompletedTask;
            this.Name = name;
            providerRuntime = (IStreamProviderRuntime) providerUtilitiesManager;
            this.runtimeClient = this.providerRuntime.ServiceProvider.GetRequiredService<IRuntimeClient>();
            fireAndForgetDelivery = config.GetBoolProperty(FIRE_AND_FORGET_DELIVERY, DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY);
            optimizeForImmutableData = config.GetBoolProperty(OPTIMIZE_FOR_IMMUTABLE_DATA, DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA);
            
            string pubSubTypeString;
            pubSubType = !config.Properties.TryGetValue(STREAM_PUBSUB_TYPE, out pubSubTypeString)
                ? DEFAULT_STREAM_PUBSUB_TYPE
                : (StreamPubSubType)Enum.Parse(typeof(StreamPubSubType), pubSubTypeString);
            if (pubSubType == StreamPubSubType.ExplicitGrainBasedAndImplicit 
                || pubSubType == StreamPubSubType.ExplicitGrainBasedOnly)
            {
                this.streamSubscriptionManager = this.providerRuntime.ServiceProvider
                    .GetService<IStreamSubscriptionManagerAdmin>().GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            }

            logger = providerRuntime.GetLogger(this.GetType().Name);
            logger.Info("Initialized SimpleMessageStreamProvider with name {0} and with property FireAndForgetDelivery: {1}, OptimizeForImmutableData: {2} " +
                "and PubSubType: {3}", Name, fireAndForgetDelivery, optimizeForImmutableData, pubSubType);
            stateManager.CommitState();
            return Task.CompletedTask;
        }

        public Task Start()
        {
            if (stateManager.PresetState(ProviderState.Started)) stateManager.CommitState();
            return Task.CompletedTask;
        }

        public Task Close()
        {
            if (stateManager.PresetState(ProviderState.Closed)) stateManager.CommitState();
            return Task.CompletedTask;
        }

        public IStreamSubscriptionManager GetStreamSubscriptionManager()
        {
            return this.streamSubscriptionManager;
        }

        public IAsyncStream<T> GetStream<T>(Guid id, string streamNamespace)
        {
            var streamId = StreamId.GetStreamId(id, Name, streamNamespace);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                streamId,
                () => new StreamImpl<T>(streamId, this, IsRewindable, this.runtimeClient));
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> stream)
        {
            return new SimpleMessageStreamProducer<T>((StreamImpl<T>)stream, Name, providerRuntime,
                fireAndForgetDelivery, optimizeForImmutableData, providerRuntime.PubSub(pubSubType), IsRewindable,
                this.runtimeClient.SerializationManager);
        }

        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            return GetConsumerInterfaceImpl(streamId);
        }

        private IInternalAsyncObservable<T> GetConsumerInterfaceImpl<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime,
                providerRuntime.PubSub(pubSubType), IsRewindable);
        }
    }
}
