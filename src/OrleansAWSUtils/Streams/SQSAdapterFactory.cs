﻿using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

namespace OrleansAWSUtils.Streams
{
    /// <summary> Factory class for Azure Queue based stream provider.</summary>
    public class SQSAdapterFactory : IQueueAdapterFactory
    {
        internal const int CacheSizeDefaultValue = 4096;
        internal const string NumQueuesPropertyName = "NumQueues";

        /// <summary> Default number of Azure Queue used in this stream provider.</summary>
        public const int NumQueuesDefaultValue = 8; // keep as power of 2.

        private string deploymentId;
        private string dataConnectionString;
        private string providerName;
        private int cacheSize;
        private int numQueues;
        private HashRingBasedStreamQueueMapper streamQueueMapper;
        private IQueueAdapterCache adapterCache;
        private SerializationManager serializationManager;
        private ILoggerFactory loggerFactory;
        /// <summary>"DataConnectionString".</summary>
        public const string DataConnectionStringPropertyName = "DataConnectionString";
        /// <summary>"DeploymentId".</summary>
        public const string DeploymentIdPropertyName = "DeploymentId";

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        /// <summary> Init the factory.</summary>
        public virtual void Init(IProviderConfiguration config, string providerName, IServiceProvider serviceProvider)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (!config.Properties.TryGetValue(DataConnectionStringPropertyName, out dataConnectionString))
                throw new ArgumentException(string.Format("{0} property not set", DataConnectionStringPropertyName));
            if (!config.Properties.TryGetValue(DeploymentIdPropertyName, out deploymentId))
                throw new ArgumentException(string.Format("{0} property not set", DeploymentIdPropertyName));

            cacheSize = SimpleQueueAdapterCache.ParseSize(config, CacheSizeDefaultValue);
            this.loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            numQueues = config.GetIntProperty(NumQueuesPropertyName, NumQueuesDefaultValue);

            this.providerName = providerName;
            streamQueueMapper = new HashRingBasedStreamQueueMapper(numQueues, providerName);
            adapterCache = new SimpleQueueAdapterCache(cacheSize, providerName, serviceProvider.GetRequiredService<ILoggerFactory>());
            this.serializationManager = serviceProvider.GetRequiredService<SerializationManager>();
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }
        }

        /// <summary>Creates the Azure Queue based adapter.</summary>
        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SQSAdapter(this.serializationManager, streamQueueMapper, this.loggerFactory, dataConnectionString, deploymentId, providerName);
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
    }
}
