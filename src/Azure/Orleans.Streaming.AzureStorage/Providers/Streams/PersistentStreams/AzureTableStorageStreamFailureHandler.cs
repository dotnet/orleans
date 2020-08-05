using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.AzureStorage;
using Orleans.Streams;

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
        private readonly string clusterId;
        private readonly AzureTableDataManager<TEntity> dataManager;
        private readonly Func<TEntity> createEntity;

        /// <summary>
        /// Delivery failure handler that writes failures to azure table storage.
        /// </summary>
        /// <param name="serializationManager"></param>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="faultOnFailure"></param>
        /// <param name="clusterId"></param>
        /// <param name="tableName"></param>
        /// <param name="storageConnectionString"></param>
        /// <param name="createEntity"></param>
        public AzureTableStorageStreamFailureHandler(SerializationManager serializationManager, ILoggerFactory loggerFactory, bool faultOnFailure, string clusterId, string tableName, string storageConnectionString, Func<TEntity> createEntity = null)
            : this (serializationManager, loggerFactory, faultOnFailure, clusterId, new AzureStorageOperationOptions { TableName = tableName, ConnectionString = storageConnectionString }, createEntity)
        {
        }

        /// <summary>
        /// Delivery failure handler that writes failures to azure table storage.
        /// </summary>
        /// <param name="serializationManager"></param>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="faultOnFailure"></param>
        /// <param name="clusterId"></param>
        /// <param name="azureStorageOptions"></param>
        /// <param name="createEntity"></param>
        public AzureTableStorageStreamFailureHandler(SerializationManager serializationManager, ILoggerFactory loggerFactory, bool faultOnFailure, string clusterId, AzureStorageOperationOptions azureStorageOptions, Func<TEntity> createEntity = null)
        {
            if (string.IsNullOrEmpty(clusterId))
            {
                throw new ArgumentNullException(nameof(clusterId));
            }
            if (string.IsNullOrEmpty(azureStorageOptions.TableName))
            {
                throw new ArgumentNullException(nameof(azureStorageOptions.TableName));
            }
            if (string.IsNullOrEmpty(azureStorageOptions.ConnectionString))
            {
                throw new ArgumentNullException(nameof(azureStorageOptions.ConnectionString));
            }
            this.serializationManager = serializationManager;
            this.clusterId = clusterId;
            ShouldFaultSubsriptionOnError = faultOnFailure;
            this.createEntity = createEntity ?? DefaultCreateEntity;
            dataManager = new AzureTableDataManager<TEntity>(
                azureStorageOptions,
                loggerFactory.CreateLogger<AzureTableDataManager<TEntity>>());
        }

        /// <summary>
        /// Indicates if the subscription should be put in a faulted state upon stream failures
        /// </summary>
        public bool ShouldFaultSubsriptionOnError { get; private set; }

        /// <summary>
        /// Initialization
        /// </summary>
        /// <returns></returns>
        public Task InitAsync()
        {
            return dataManager.InitTableAsync();
        }

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
        /// <param name="subscriptionId"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
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
            await dataManager.CreateTableEntryAsync(failureEntity);
        }
    }
}
