using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;

namespace OrleansAWSUtils.Streams
{
    /// <summary>Factory class for the Amazon SQS stream provider.</summary>
    public class SQSAdapterFactory : IQueueAdapterFactory
    {
        private readonly string providerName;
        private readonly SqsOptions sqsOptions;
        private readonly ClusterOptions clusterOptions;
        private readonly ISQSDataAdapter dataAdapter;
        private readonly ILoggerFactory loggerFactory;
        private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
        private readonly IQueueAdapterCache adapterCache;

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; } = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="SQSAdapterFactory"/> class.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="sqsOptions">The SQS options.</param>
        /// <param name="queueMapperOptions">The stream-to-queue mapping options.</param>
        /// <param name="cacheOptions">The receiver cache options.</param>
        /// <param name="clusterOptions">The cluster options.</param>
        /// <param name="dataAdapter">The adapter used to convert between Orleans stream batches and SQS messages.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SQSAdapterFactory(
            string name,
            SqsOptions sqsOptions,
            HashRingStreamQueueMapperOptions queueMapperOptions,
            SimpleQueueCacheOptions cacheOptions,
            IOptions<ClusterOptions> clusterOptions,
            ISQSDataAdapter dataAdapter,
            ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(sqsOptions);
            ArgumentNullException.ThrowIfNull(queueMapperOptions);
            ArgumentNullException.ThrowIfNull(cacheOptions);
            ArgumentNullException.ThrowIfNull(clusterOptions);
            ArgumentNullException.ThrowIfNull(dataAdapter);
            ArgumentNullException.ThrowIfNull(loggerFactory);

            this.providerName = name;
            this.sqsOptions = sqsOptions;
            this.clusterOptions = clusterOptions.Value;
            this.dataAdapter = dataAdapter;
            this.loggerFactory = loggerFactory;
            streamQueueMapper = new HashRingBasedStreamQueueMapper(queueMapperOptions, this.providerName);

            adapterCache = new SimpleQueueAdapterCache(cacheOptions, this.providerName, this.loggerFactory);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SQSAdapterFactory"/> class using the default SQS data adapter.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="sqsOptions">The SQS options.</param>
        /// <param name="queueMapperOptions">The stream-to-queue mapping options.</param>
        /// <param name="cacheOptions">The receiver cache options.</param>
        /// <param name="clusterOptions">The cluster options.</param>
        /// <param name="serializer">The serializer used by the default SQS data adapter.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SQSAdapterFactory(
            string name,
            SqsOptions sqsOptions,
            HashRingStreamQueueMapperOptions queueMapperOptions,
            SimpleQueueCacheOptions cacheOptions,
            IOptions<ClusterOptions> clusterOptions,
            Serializer serializer,
            ILoggerFactory loggerFactory)
            : this(
                name,
                sqsOptions,
                queueMapperOptions,
                cacheOptions,
                clusterOptions,
                new SQSDataAdapter(serializer),
                loggerFactory)
        {
        }

        /// <summary> Init the factory.</summary>
        public virtual void Init()
        {
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }
        }

        /// <summary>Creates the Amazon SQS queue adapter.</summary>
        /// <returns>A task containing the queue adapter.</returns>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SQSAdapter(this.dataAdapter, this.streamQueueMapper, this.loggerFactory, this.sqsOptions, this.clusterOptions.ServiceId, this.providerName);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        /// <summary>Creates the adapter cache.</summary>
        /// <returns>The adapter cache.</returns>
        public virtual IQueueAdapterCache GetQueueAdapterCache()
        {
            return adapterCache;
        }

        /// <summary>Creates the factory stream queue mapper.</summary>
        /// <returns>The stream queue mapper.</returns>
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            return streamQueueMapper;
        }

        /// <summary>
        /// Creates a delivery failure handler for the specified queue.
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <returns>A task containing the delivery failure handler.</returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return StreamFailureHandlerFactory(queueId);
        }

        /// <summary>
        /// Creates and initializes an SQS adapter factory using services registered for the named stream provider.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <returns>The initialized adapter factory.</returns>
        public static SQSAdapterFactory Create(IServiceProvider services, string name)
        {
            var sqsOptions = services.GetOptionsByName<SqsOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(name);
            var dataAdapter = services.GetKeyedService<ISQSDataAdapter>(name)
                               ?? services.GetService<ISQSDataAdapter>()
                               ?? ActivatorUtilities.CreateInstance<SQSDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<SQSAdapterFactory>(services, name, sqsOptions, queueMapperOptions, cacheOptions, clusterOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}
