using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Serialization;
using Orleans.Configuration;
using Orleans.Streams.Filtering;

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
        private readonly IStreamFilter streamFilter;

        public bool IsRewindable { get { return false; } }

        public SimpleMessageStreamProvider(
            string name,
            SimpleMessageStreamProviderOptions options,
            IStreamFilter streamFilter,
            ILoggerFactory loggerFactory,
            IServiceProvider services,
            SerializationManager serializationManager)
        {
            this.loggerFactory = loggerFactory;
            this.Name = name;
            this.logger = loggerFactory.CreateLogger<SimpleMessageStreamProvider>();
            this.options = options;
            this.streamFilter = streamFilter;
            this.providerRuntime = services.GetRequiredService<IStreamProviderRuntime>();
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

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            var id = new InternalStreamId(Name, streamId);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                id,
                () => new StreamImpl<T>(id, this, IsRewindable, this.runtimeClient));
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> stream)
        {
            return new SimpleMessageStreamProducer<T>(
                (StreamImpl<T>)stream,
                Name,
                providerRuntime,
                this.options.FireAndForgetDelivery,
                this.options.OptimizeForImmutableData,
                providerRuntime.PubSub(this.options.PubSubType),
                this.streamFilter,
                IsRewindable,
                this.serializationManager,
                this.loggerFactory.CreateLogger<SimpleMessageStreamProducer<T>>());
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
            return ActivatorUtilities.CreateInstance<SimpleMessageStreamProvider>(
                services,
                name,
                services.GetRequiredService<IOptionsMonitor<SimpleMessageStreamProviderOptions>>().Get(name),
                services.GetServiceByName<IStreamFilter>(name) ?? new NoOpStreamFilter());
        }
    }
}
