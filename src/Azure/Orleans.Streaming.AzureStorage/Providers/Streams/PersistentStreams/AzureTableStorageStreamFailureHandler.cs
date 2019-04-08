using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streaming.AzureStorage;

namespace Orleans.Providers.Streams.PersistentStreams
{
    /// <summary>
    /// Delivery failure handler that writes failures to azure table storage.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    public class AzureTableStorageStreamFailureHandler<TEntity> : IStreamFailureHandler where TEntity : StreamDeliveryFailureEntity, new()
    {
        private static readonly Func<TEntity> DefaultCreateEntity = () => new TEntity();
        private readonly SerializationManager serializationManager;
        private readonly DataManagerResolver dataManagerResolver;
        private readonly string clusterId;
        private readonly Func<TEntity> createEntity;

        /// <summary>
        /// Delivery failure handler that writes failures to azure table storage.
        /// </summary>
        public AzureTableStorageStreamFailureHandler(SerializationManager serializationManager, ILoggerFactory loggerFactory, bool faultOnFailure, string clusterId, string tableName, string storageConnectionString, Func<TEntity> createEntity = null)
        {
            if (string.IsNullOrEmpty(clusterId))
            {
                throw new ArgumentNullException(nameof(clusterId));
            }
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentNullException("tableName");
            }
            if (string.IsNullOrEmpty(storageConnectionString))
            {
                throw new ArgumentNullException("storageConnectionString");
            }
            this.serializationManager = serializationManager;
            this.clusterId = clusterId;
            ShouldFaultSubscriptionOnError = faultOnFailure;
            this.createEntity = createEntity ?? DefaultCreateEntity;
            this.dataManagerResolver = new DataManagerResolver(new AzureTableDataManager<TEntity>(tableName, storageConnectionString, loggerFactory));
        }

        /// <summary>
        /// Indicates if the subscription should be put in a faulted state upon stream failures
        /// </summary>
        public bool ShouldFaultSubscriptionOnError { get; private set; }

        /// <summary>
        /// Should be called when an event could not be delivered to a consumer, after exhausting retry attempts.
        /// </summary>
        /// <param name="subscriptionId"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            return OnFailure(subscriptionId, streamProviderName, streamIdentity, sequenceToken);
        }

        /// <summary>
        /// Should be called when a subscription requested by a consumer could not be setup, after exhausting retry attempts.
        /// </summary>
        public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            return OnFailure(subscriptionId, streamProviderName, streamIdentity, sequenceToken);
        }

        private async Task OnFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
                StreamSequenceToken sequenceToken)
        {
            if (subscriptionId == null)
            {
                throw new ArgumentNullException("subscriptionId");
            }
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            if (streamIdentity == null)
            {
                throw new ArgumentNullException("streamIdentity");
            }

            var failureEntity = createEntity();
            failureEntity.SubscriptionId = subscriptionId.Guid;
            failureEntity.StreamProviderName = streamProviderName;
            failureEntity.StreamGuid = streamIdentity.Guid;
            failureEntity.StreamNamespace = streamIdentity.Namespace;
            failureEntity.SetSequenceToken(this.serializationManager, sequenceToken);
            failureEntity.SetPartitionKey(this.clusterId);
            failureEntity.SetRowkey();
            AzureTableDataManager<TEntity> dataManager = await this.dataManagerResolver.GetDataManager();
            await dataManager.CreateTableEntryAsync(failureEntity);
        }

        private class DataManagerResolver
        {
            private readonly AzureTableDataManager<TEntity> dataManager;
            private Task dataManagerInitPending;
            private bool dataManagerInitialized;

            public DataManagerResolver(AzureTableDataManager<TEntity> dataManager)
            {
                this.dataManager = dataManager;
            }

            public async Task<AzureTableDataManager<TEntity>> GetDataManager()
            {
                if (!dataManagerInitialized)
                {
                    Task pending = this.dataManagerInitPending ?? (this.dataManagerInitPending = this.dataManager.InitTableAsync());
                    try
                    {
                        await pending;
                        this.dataManagerInitialized = true;
                    }
                    catch
                    {
                        this.dataManagerInitPending = null;
                        throw;
                    }
                }
                return this.dataManager;
           }
        }
    }
}
