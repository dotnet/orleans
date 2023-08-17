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
        private readonly Serializer<StreamSequenceToken> serializer;
        private readonly string clusterId;
        private readonly AzureTableDataManager<TEntity> dataManager;
        private readonly Func<TEntity> createEntity;

        /// <summary>
        /// Delivery failure handler that writes failures to azure table storage.
        /// </summary>
        /// <param name="serializer"></param>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="faultOnFailure"></param>
        /// <param name="clusterId"></param>
        /// <param name="azureStorageOptions"></param>
        /// <param name="createEntity"></param>
        public AzureTableStorageStreamFailureHandler(Serializer<StreamSequenceToken> serializer, ILoggerFactory loggerFactory, bool faultOnFailure, string clusterId, AzureStorageOperationOptions azureStorageOptions, Func<TEntity> createEntity = null)
        {
            if (string.IsNullOrEmpty(clusterId))
            {
                throw new ArgumentNullException(nameof(clusterId));
            }
            if (string.IsNullOrEmpty(azureStorageOptions.TableName))
            {
                throw new ArgumentNullException(nameof(azureStorageOptions.TableName));
            }

            this.serializer = serializer;
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
        /// <param name="streamId"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, StreamId streamId,
            StreamSequenceToken sequenceToken)
        {
            return OnFailure(subscriptionId, streamProviderName, streamId, sequenceToken);
        }

        /// <summary>
        /// Should be called when a subscription requested by a consumer could not be setup, after exhausting retry attempts.
        /// </summary>
        /// <param name="subscriptionId"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="streamId"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public Task OnSubscriptionFailure(GuidId subscriptionId, string streamProviderName, StreamId streamId,
            StreamSequenceToken sequenceToken)
        {
            return OnFailure(subscriptionId, streamProviderName, streamId, sequenceToken);
        }

        private async Task OnFailure(GuidId subscriptionId, string streamProviderName, StreamId streamId,
                StreamSequenceToken sequenceToken)
        {
            if (subscriptionId == null)
            {
                throw new ArgumentNullException(nameof(subscriptionId));
            }
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException(nameof(streamProviderName));
            }

            var failureEntity = createEntity();
            failureEntity.SubscriptionId = subscriptionId.Guid;
            failureEntity.StreamProviderName = streamProviderName;
            failureEntity.StreamGuid = streamId.GetKeyAsString();
            failureEntity.StreamNamespace = streamId.GetNamespace();
            failureEntity.SetSequenceToken(this.serializer, sequenceToken);
            failureEntity.SetPartitionKey(this.clusterId);
            failureEntity.SetRowkey();
            await dataManager.CreateTableEntryAsync(failureEntity);
        }
    }
}
