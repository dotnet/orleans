using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Stream generator commands
    /// </summary>
    public enum StreamGeneratorCommand
    {
        /// <summary>
        /// Command to configure the generator
        /// </summary>
        Configure = PersistentStreamProviderCommand.AdapterFactoryCommandStartRange
    }

    /// <summary>
    /// Adapter factory for stream generator stream provider.
    /// This factory acts as the adapter and the adapter factory.  It creates receivers that use configurable generator
    ///   to generate event streams, rather than reading them from storage.
    /// </summary>
    public class GeneratorAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache, IControllable
    {
        /// <summary>
        /// Configuration property name for generator configuration type
        /// </summary>
        private readonly HashRingStreamQueueMapperOptions queueMapperOptions;
        private readonly StreamStatisticOptions statisticOptions;
        private readonly IServiceProvider serviceProvider;
        private readonly Serialization.Serializer serializer;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<GeneratorAdapterFactory> logger;
        private IStreamGeneratorConfig generatorConfig;
        private IStreamQueueMapper streamQueueMapper;
        private IStreamFailureHandler streamFailureHandler;
        private ConcurrentDictionary<QueueId, Receiver> receivers;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private BlockPoolMonitorDimensions blockPoolMonitorDimensions;

        /// <inheritdoc />
        public bool IsRewindable => true;

        /// <inheritdoc />
        public StreamProviderDirection Direction => StreamProviderDirection.ReadOnly;

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>
        /// Create a cache monitor to report cache related metrics
        /// Return a ICacheMonitor
        /// </summary>
        protected Func<CacheMonitorDimensions, ICacheMonitor> CacheMonitorFactory;

        /// <summary>
        /// Create a block pool monitor to monitor block pool related metrics
        /// Return a IBlockPoolMonitor
        /// </summary>
        protected Func<BlockPoolMonitorDimensions, IBlockPoolMonitor> BlockPoolMonitorFactory;

        /// <summary>
        /// Create a monitor to monitor QueueAdapterReceiver related metrics
        /// Return a IQueueAdapterReceiverMonitor
        /// </summary>
        protected Func<ReceiverMonitorDimensions, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;
        
        public GeneratorAdapterFactory(
            string providerName,
            HashRingStreamQueueMapperOptions queueMapperOptions,
            StreamStatisticOptions statisticOptions,
            IServiceProvider serviceProvider,
            Serialization.Serializer serializer,
            ILoggerFactory loggerFactory)
        {
            this.Name = providerName;
            this.queueMapperOptions = queueMapperOptions ?? throw new ArgumentNullException(nameof(queueMapperOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.logger = loggerFactory.CreateLogger<GeneratorAdapterFactory>();
        }

        /// <summary>
        /// Initializes the factory.
        /// </summary>
        public void Init()
        {
            this.receivers = new ConcurrentDictionary<QueueId, Receiver>();
            if (CacheMonitorFactory == null)
                this.CacheMonitorFactory = (dimensions) => new DefaultCacheMonitor(dimensions);
            if (this.BlockPoolMonitorFactory == null)
                this.BlockPoolMonitorFactory = (dimensions) => new DefaultBlockPoolMonitor(dimensions);
            if (this.ReceiverMonitorFactory == null)
                this.ReceiverMonitorFactory = (dimensions) => new DefaultQueueAdapterReceiverMonitor(dimensions);
            generatorConfig = this.serviceProvider.GetServiceByName<IStreamGeneratorConfig>(this.Name);
            if(generatorConfig == null)
            {
                this.logger.LogInformation("No generator configuration found for stream provider {StreamProvider}.  Inactive until provided with configuration by command.", this.Name);
            }
        }

        private void CreateBufferPoolIfNotCreatedYet()
        {
            if (this.bufferPool == null)
            {
                // 1 meg block size pool
                this.blockPoolMonitorDimensions = new BlockPoolMonitorDimensions($"BlockPool-{Guid.NewGuid()}");
                var oneMb = 1 << 20;
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(blockPoolMonitorDimensions), oneMb);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneMb), objectPoolMonitor, this.statisticOptions.StatisticMonitorWriteInterval);
            }
        }

        /// <inheritdoc />
        public Task<IQueueAdapter> CreateAdapter()
        {
            return Task.FromResult<IQueueAdapter>(this);
        }

        /// <inheritdoc />
        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        /// <inheritdoc />
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            return streamQueueMapper ?? (streamQueueMapper = new HashRingBasedStreamQueueMapper(this.queueMapperOptions, this.Name));
        }

        /// <inheritdoc />
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return Task.FromResult(streamFailureHandler ?? (streamFailureHandler = new NoOpStreamDeliveryFailureHandler()));
        }

        /// <inheritdoc />
        public Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            if (!receivers.TryGetValue(queueId, out var receiver))
            {
                var dimensions = new ReceiverMonitorDimensions(queueId.ToString());
                var receiverMonitor = this.ReceiverMonitorFactory(dimensions);
                receiver = receivers.GetOrAdd(queueId, new Receiver(receiverMonitor));
            }
            SetGeneratorOnReceiver(receiver);
            return receiver;
        }

        /// <inheritdoc />
        public Task<object> ExecuteCommand(int command, object arg)
        {
            if (arg == null)
            {
                throw new ArgumentNullException("arg");
            }
            generatorConfig = arg as IStreamGeneratorConfig;
            if (generatorConfig == null)
            {
                throw new ArgumentOutOfRangeException("arg", "Arg must by of type IStreamGeneratorConfig");
            }

            // update generator on receivers
            foreach (var receiver in receivers)
            {
                SetGeneratorOnReceiver(receiver.Value);
            }

            return Task.FromResult<object>(true);
        }

        private class Receiver : IQueueAdapterReceiver
        {
            const int MaxDelayMs = 20;
            private readonly IQueueAdapterReceiverMonitor receiverMonitor;
            public IStreamGenerator QueueGenerator { private get; set; }

            public Receiver(IQueueAdapterReceiverMonitor receiverMonitor)
            {
                this.receiverMonitor = receiverMonitor;
            }

            public Task Initialize(TimeSpan timeout)
            {
                this.receiverMonitor?.TrackInitialization(true, TimeSpan.MinValue, null);
                return Task.CompletedTask;
            }

            public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            {
                var watch = Stopwatch.StartNew();
                await Task.Delay(Random.Shared.Next(1,MaxDelayMs));
                List<IBatchContainer> batches;
                if (QueueGenerator == null || !QueueGenerator.TryReadEvents(DateTime.UtcNow, maxCount, out batches))
                {
                    return new List<IBatchContainer>();
                }
                watch.Stop();
                this.receiverMonitor?.TrackRead(true, watch.Elapsed, null);
                if (batches.Count > 0)
                {
                    var oldestMessage = batches[0] as GeneratedBatchContainer;
                    var newestMessage = batches[batches.Count - 1] as GeneratedBatchContainer;
                    this.receiverMonitor?.TrackMessagesReceived(batches.Count, oldestMessage?.EnqueueTimeUtc, newestMessage?.EnqueueTimeUtc);
                }
                return batches;
            }

            public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
            {
                return Task.CompletedTask;
            }

            public Task Shutdown(TimeSpan timeout)
            {
                this.receiverMonitor?.TrackShutdown(true, TimeSpan.MinValue, null);
                return Task.CompletedTask;
            }
        }

        private void SetGeneratorOnReceiver(Receiver receiver)
        {
            // if we don't have generator configuration, don't set generator
            if (generatorConfig == null)
            {
                return;
            }

            var generator = (IStreamGenerator)(serviceProvider?.GetService(generatorConfig.StreamGeneratorType) ?? Activator.CreateInstance(generatorConfig.StreamGeneratorType));
            if (generator == null)
            {
                throw new OrleansException($"StreamGenerator type not supported: {generatorConfig.StreamGeneratorType}");
            }
            generator.Configure(serviceProvider, generatorConfig);
            receiver.QueueGenerator = generator;
        }

        /// <inheritdoc />
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            //move block pool creation from init method to here, to avoid unnecessary block pool creation when stream provider is initialized in client side.
            CreateBufferPoolIfNotCreatedYet();
            var dimensions = new CacheMonitorDimensions(queueId.ToString(), this.blockPoolMonitorDimensions.BlockPoolId);
            var cacheMonitor = this.CacheMonitorFactory(dimensions);
            return new GeneratorPooledCache(
                bufferPool,
                this.loggerFactory.CreateLogger($"{typeof(GeneratorPooledCache).FullName}.{this.Name}.{queueId}"),
                serializer,
                cacheMonitor,
                this.statisticOptions.StatisticMonitorWriteInterval);
        }

        /// <summary>
        /// Creates a new <see cref="GeneratorAdapterFactory"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The newly created <see cref="GeneratorAdapterFactory"/> instance.</returns>
        public static GeneratorAdapterFactory Create(IServiceProvider services, string name)
        {
            var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var factory = ActivatorUtilities.CreateInstance<GeneratorAdapterFactory>(services, name, queueMapperOptions, statisticOptions);
            factory.Init();
            return factory;
        }
    }
}
