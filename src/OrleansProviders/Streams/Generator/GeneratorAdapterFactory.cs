
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        public const string GeneratorConfigTypeName = "StreamGeneratorConfigType";
        private IServiceProvider serviceProvider;
        private GeneratorAdapterConfig adapterConfig;
        private IStreamGeneratorConfig generatorConfig;
        private IStreamQueueMapper streamQueueMapper;
        private IStreamFailureHandler streamFailureHandler;
        private ConcurrentDictionary<QueueId, Receiver> receivers;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private Logger logger;

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
        /// Initialize the factory
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="providerName"></param>
        /// <param name="log"></param>
        /// <param name="svcProvider"></param>
        public void Init(IProviderConfiguration providerConfig, string providerName, Logger log, IServiceProvider svcProvider)
        {
            logger = log;
            serviceProvider = svcProvider;
            receivers = new ConcurrentDictionary<QueueId, Receiver>();
            adapterConfig = new GeneratorAdapterConfig(providerName);
            adapterConfig.PopulateFromProviderConfig(providerConfig);
            if (adapterConfig.GeneratorConfigType != null)
            {
                generatorConfig = (IStreamGeneratorConfig)(serviceProvider?.GetService(adapterConfig.GeneratorConfigType) ?? Activator.CreateInstance(adapterConfig.GeneratorConfigType));
                if (generatorConfig == null)
                {
                    throw new ArgumentOutOfRangeException(nameof(providerConfig), "GeneratorConfigType not valid.");
                }
                generatorConfig.PopulateFromProviderConfig(providerConfig);
            }
            // 10 meg buffer pool.  10 1 meg blocks
            bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(10, () => new FixedSizeBuffer(1<<20));
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
            return streamQueueMapper ?? (streamQueueMapper = new HashRingBasedStreamQueueMapper(adapterConfig.TotalQueueCount, adapterConfig.StreamProviderName));
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
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        public string Name => adapterConfig.StreamProviderName;

        /// <summary>
        /// Stores a batch of messages
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="streamGuid"></param>
        /// <param name="streamNamespace"></param>
        /// <param name="events"></param>
        /// <param name="token"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        public Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            return TaskDone.Done;
        }

        /// <summary>
        /// Creates a quere receiver for the specificed queueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            Receiver receiver = receivers.GetOrAdd(queueId, qid => new Receiver());
            SetGeneratorOnReciever(receiver);
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

            // update generator on recievers
            foreach (Receiver receiver in receivers.Values)
            {
                SetGeneratorOnReciever(receiver);
            }

            return Task.FromResult<object>(true);
        }

        private class Receiver : IQueueAdapterReceiver
        {
            const int MaxDelayMs = 20;
            private readonly Random random = new Random((int)DateTime.UtcNow.Ticks % int.MaxValue);

            public IStreamGenerator QueueGenerator { private get; set; }

            public Task Initialize(TimeSpan timeout)
            {
                return TaskDone.Done;
            }

            public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            {
                await Task.Delay(random.Next(1,MaxDelayMs));
                List<IBatchContainer> batches;
                if (QueueGenerator == null || !QueueGenerator.TryReadEvents(DateTime.UtcNow, out batches))
                {
                    return new List<IBatchContainer>();
                }
                return batches;
            }

            public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
            {
                return TaskDone.Done;
            }

            public Task Shutdown(TimeSpan timeout)
            {
                return TaskDone.Done;
            }
        }

        private void SetGeneratorOnReciever(Receiver receiver)
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
            return new GeneratorPooledCache(bufferPool, logger);
        }
    }
}
