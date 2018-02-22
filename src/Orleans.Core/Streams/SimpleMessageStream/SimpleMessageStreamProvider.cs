using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Serialization;
using Orleans.Configuration;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    public class SimpleMessageStreamProvider : IInternalStreamProvider, IStreamProvider, IStreamSubscriptionManagerRetriever
    {
        public string                       Name { get; private set; }

        private ILogger                      logger;
        private IStreamProviderRuntime      providerRuntime;
        private IRuntimeClient              runtimeClient;
        private IStreamSubscriptionManager  streamSubscriptionManager;
        private ILoggerFactory              loggerFactory;
        private SerializationManager        serializationManager;
        private SimpleMessageStreamProviderOptions options;
        public bool IsRewindable { get { return false; } }

        public SimpleMessageStreamProvider(string name, SimpleMessageStreamProviderOptions options,
            ILoggerFactory loggerFactory, IProviderRuntime providerRuntime, SerializationManager serializationManager)
        {
            this.loggerFactory = loggerFactory;
            this.Name = name;
            this.logger = loggerFactory.CreateLogger($"{this.GetType().FullName}.{name}");
            this.options = options;
            this.providerRuntime = providerRuntime as IStreamProviderRuntime;
            this.runtimeClient = providerRuntime.ServiceProvider.GetService<IRuntimeClient>();
            this.serializationManager = serializationManager;
            if (this.options.PubSubType == StreamPubSubType.ExplicitGrainBasedAndImplicit
                || this.options.PubSubType == StreamPubSubType.ExplicitGrainBasedOnly)
            {
                this.streamSubscriptionManager = this.providerRuntime.ServiceProvider
                    .GetService<IStreamSubscriptionManagerAdmin>()
                    .GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            }
            logger.Info(
                "Initialized SimpleMessageStreamProvider with name {0} and with property FireAndForgetDelivery: {1}, OptimizeForImmutableData: {2} " +
                "and PubSubType: {3}", Name, this.options.FireAndForgetDelivery, this.options.OptimizeForImmutableData,
                this.options.PubSubType);
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
                this.options.FireAndForgetDelivery, this.options.OptimizeForImmutableData, providerRuntime.PubSub(this.options.PubSubType), IsRewindable,
                this.serializationManager, this.loggerFactory);
        }

        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            return GetConsumerInterfaceImpl(streamId);
        }

        private IInternalAsyncObservable<T> GetConsumerInterfaceImpl<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime,
                providerRuntime.PubSub(this.options.PubSubType), this.logger, IsRewindable);
        }

        public static IStreamProvider Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<SimpleMessageStreamProvider>(services, name, services.GetService<IOptionsSnapshot<SimpleMessageStreamProviderOptions>>().Get(name));
        }
    }
}
