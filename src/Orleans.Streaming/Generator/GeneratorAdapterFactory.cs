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
using Orleans.Serialization;
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
        private readonly SerializationManager serializationManager;
        private readonly ITelemetryProducer telemetryProducer;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<GeneratorAdapterFactory> logger;
        private IStreamGeneratorConfig generatorConfig;
        private IStreamQueueMapper streamQueueMapper;
        private IStreamFailureHandler streamFailureHandler;
        private ConcurrentDictionary<QueueId, Receiver> receivers;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private BlockPoolMonitorDimensions blockPoolMonitorDimensions;
        /// <summary>
        /// Determines whether this is a rewindable stream adapter - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream adapter, false otherwise.</returns>
        public bool IsRewindable => true;

        /// <summary>
        /// Direction of this queue adapter: Read, Write or ReadWrite.
        /// </summary>
        /// <returns>The direction in which this adapter provides data.</returns>
        public StreamProviderDirection Direction => StreamProviderDirection.ReadOnly;

        /// <summary>
        /// Name of the adapter. From IQueueAdapter.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Create a cache monitor to report cache related metrics
        /// Return a ICacheMonitor
        /// </summary>
        protected Func<CacheMonitorDimensions, ITelemetryProducer, ICacheMonitor> CacheMonitorFactory;

        /// <summary>
        /// Create a block pool monitor to monitor block pool related metrics
        /// Return a IBlockPoolMonitor
        /// </summary>
        protected Func<BlockPoolMonitorDimensions, ITelemetryProducer, IBlockPoolMonitor> BlockPoolMonitorFactory;

        /// <summary>
        /// Create a monitor to monitor QueueAdapterReceiver related metrics
        /// Return a IQueueAdapterReceiverMonitor
        /// </summary>
        protected Func<ReceiverMonitorDimensions, ITelemetryProducer, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;

        public GeneratorAdapterFactory(string providerName, HashRingStreamQueueMapperOptions queueMapperOptions, StreamStatisticOptions statisticOptions, IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
        {
            this.Name = providerName;
            this.queueMapperOptions = queueMapperOptions ?? throw new ArgumentNullException(nameof(queueMapperOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.serializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            this.telemetryProducer = telemetryProducer ?? throw new ArgumentNullException(nameof(telemetryProducer));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.logger = loggerFactory.CreateLogger<GeneratorAdapterFactory>();
        }

        /// <summary>
        /// Initialize the factory
        /// </summary>
        public void Init()
        {
            this.receivers = new ConcurrentDictionary<QueueId, Receiver>();
            if (CacheMonitorFactory == null)
                this.CacheMonitorFactory = (dimensions, telemetryProducer) => new DefaultCacheMonitor(dimensions, telemetryProducer);
            if (this.BlockPoolMonitorFactory == null)
                this.BlockPoolMonitorFactory = (dimensions, telemetryProducer) => new DefaultBlockPoolMonitor(dimensions, telemetryProducer);
            if (this.ReceiverMonitorFactory == null)
                this.ReceiverMonitorFactory = (dimensions, telemetryProducer) => new DefaultQueueAdapterReceiverMonitor(dimensions, telemetryProducer);
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
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(blockPoolMonitorDimensions, this.telemetryProducer), oneMb);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneMb), objectPoolMonitor, this.statisticOptions.StatisticMonitorWriteInterval);
            }
        }

        /// <summary>
        /// Create an adapter
        /// </summary>
        /// <returns></returns>
        public Task<IQueueAdapter> CreateAdapter()
        {
            return Task.FromResult<IQueueAdapter>(this);
        }

        /// <summary>
        /// Get the cache adapter
        /// </summary>
        /// <returns></returns>
        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        /// <summary>
        /// Get the stream queue mapper
        /// </summary>
        /// <returns></returns>
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            return streamQueueMapper ?? (streamQueueMapper = new HashRingBasedStreamQueueMapper(this.queueMapperOptions, this.Name));
        }

        /// <summary>
        /// Get the delivery failure handler
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return Task.FromResult(streamFailureHandler ?? (streamFailureHandler = new NoOpStreamDeliveryFailureHandler()));
        }

        /// <summary>
        /// Stores a batch of messages
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="streamId"></param>
        /// <param name="events"></param>
        /// <param name="token"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        public Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a queue receiver for the specified queueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            var dimensions = new ReceiverMonitorDimensions(queueId.ToString());
            var receiverMonitor = this.ReceiverMonitorFactory(dimensions, this.telemetryProducer);
            var receiver = receivers.GetOrAdd(queueId, new Receiver(receiverMonitor));
            SetGeneratorOnReceiver(receiver);
            return receiver;
        }

        /// <summary>
        /// A function to execute a control command.
        /// </summary>
        /// <param name="command">A serial number of the command.</param>
        /// <param name="arg">An opaque command argument</param>
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
                await Task.Delay(ThreadSafeRandom.Next(1,MaxDelayMs));
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

        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="queueId"></param>
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            //move block pool creation from init method to here, to avoid unnecessary block pool creation when stream provider is initialized in client side.
            CreateBufferPoolIfNotCreatedYet();
            var dimensions = new CacheMonitorDimensions(queueId.ToString(), this.blockPoolMonitorDimensions.BlockPoolId);
            var cacheMonitor = this.CacheMonitorFactory(dimensions, this.telemetryProducer);
            return new GeneratorPooledCache(
                bufferPool,
                this.loggerFactory.CreateLogger($"{typeof(GeneratorPooledCache).FullName}.{this.Name}.{queueId}"),
                serializationManager,
                cacheMonitor,
                this.statisticOptions.StatisticMonitorWriteInterval);
        }

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
