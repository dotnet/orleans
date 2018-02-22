using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Orleans.Serialization;
using Orleans.Configuration;

namespace OrleansAWSUtils.Streams
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class SQSAdapterFactory : IQueueAdapterFactory
    {
        private readonly string providerName;
        private readonly SqsStreamOptions options;
        private readonly SiloOptions siloOptions;
        private readonly SerializationManager serializationManager;
        private readonly ILoggerFactory loggerFactory;
        private HashRingBasedStreamQueueMapper streamQueueMapper;
        private IQueueAdapterCache adapterCache;

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public SQSAdapterFactory(string name, SqsStreamOptions options, IServiceProvider serviceProvider, IOptions<SiloOptions> siloOptions, SerializationManager serializationManager, ILoggerFactory loggerFactory)
        {
            this.providerName = name;
            this.options = options;
            this.siloOptions = siloOptions.Value;
            this.serializationManager = serializationManager;
            this.loggerFactory = loggerFactory;
        }


        /// <summary> Init the factory.</summary>
        public virtual void Init()
        {
            streamQueueMapper = new HashRingBasedStreamQueueMapper(this.options.NumQueues, this.providerName);
            adapterCache = new SimpleQueueAdapterCache(this.options.CacheSize, this.providerName, this.loggerFactory);
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }
        }

        /// <summary>Creates the Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SQSAdapter(this.serializationManager, this.streamQueueMapper, this.loggerFactory, this.options.ConnectionString, this.options.ClusterId ?? this.siloOptions.ClusterId, this.providerName);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        /// <summary>Creates the adapter cache.</summary>
        public virtual IQueueAdapterCache GetQueueAdapterCache()
        {
            return adapterCache;
        }

        /// <summary>Creates the factory stream queue mapper.</summary>
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            return streamQueueMapper;
        }

        /// <summary>
        /// Creates a delivery failure handler for the specified queue.
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return StreamFailureHandlerFactory(queueId);
        }

        public static SQSAdapterFactory Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<SqsStreamOptions> streamOptionsSnapshot = services.GetRequiredService<IOptionsSnapshot<SqsStreamOptions>>();
            var factory = ActivatorUtilities.CreateInstance<SQSAdapterFactory>(services, name, streamOptionsSnapshot.Get(name));
            factory.Init();
            return factory;
        }
    }
}
