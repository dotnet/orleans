using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Configuration;
using Orleans.Streams.Filtering;
using Orleans.Serialization;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    /// <summary>
    /// A stream provider which uses direct grain messaging.
    /// </summary>
    public class SimpleMessageStreamProvider : IInternalStreamProvider, IStreamProvider, IStreamSubscriptionManagerRetriever
    {
        /// <inheritdoc/>
        public string                       Name { get; private set; }

        private ILogger                      logger;
        private IStreamProviderRuntime      providerRuntime;
        private IRuntimeClient              runtimeClient;
        private IStreamSubscriptionManager  streamSubscriptionManager;
        private ILoggerFactory              loggerFactory;
        private SimpleMessageStreamProviderOptions options;
        private readonly IStreamFilter streamFilter;

        /// <inheritdoc/>
        public bool IsRewindable { get { return false; } }

        /// <summary>
        /// Initializes a new instance of the <see cref="SimpleMessageStreamProvider"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="options">The options.</param>
        /// <param name="streamFilter">The stream filter.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="services">The services.</param>
        public SimpleMessageStreamProvider(
            string name,
            SimpleMessageStreamProviderOptions options,
            IStreamFilter streamFilter,
            ILoggerFactory loggerFactory,
            IServiceProvider services)
        {
            this.loggerFactory = loggerFactory;
            this.Name = name;
            this.logger = loggerFactory.CreateLogger<SimpleMessageStreamProvider>();
            this.options = options;
            this.streamFilter = streamFilter;
            this.providerRuntime = services.GetRequiredService<IStreamProviderRuntime>();
            this.runtimeClient = providerRuntime.ServiceProvider.GetService<IRuntimeClient>();
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

        /// <inheritdoc/>
        public IStreamSubscriptionManager GetStreamSubscriptionManager()
        {
            return this.streamSubscriptionManager;
        }

        /// <inheritdoc/>
        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            var id = new InternalStreamId(Name, streamId);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                id,
                () => new StreamImpl<T>(id, this, IsRewindable, this.runtimeClient));
        }

        /// <inheritdoc/>
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
                this.runtimeClient.ServiceProvider.GetRequiredService<DeepCopier<T>>(),
                this.loggerFactory.CreateLogger<SimpleMessageStreamProducer<T>>());
        }

        /// <inheritdoc/>
        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            return GetConsumerInterfaceImpl(streamId);
        }

        private IInternalAsyncObservable<T> GetConsumerInterfaceImpl<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime,
                providerRuntime.PubSub(this.options.PubSubType), this.logger, IsRewindable);
        }

        /// <summary>
        /// Creates a new <see cref="SimpleMessageStreamProvider"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The new provider instance.</returns>
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
