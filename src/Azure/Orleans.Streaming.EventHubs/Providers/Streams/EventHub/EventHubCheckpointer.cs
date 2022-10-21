using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Streaming.EventHubs;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Overrides;

namespace Orleans.Streaming.EventHubs
{
    public class EventHubCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly string providerName;
        private readonly AzureTableStreamCheckpointerOptions options;
        private readonly ClusterOptions clusterOptions;

        public EventHubCheckpointerFactory(string providerName, AzureTableStreamCheckpointerOptions options, IOptions<ClusterOptions> clusterOptions, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.loggerFactory = loggerFactory;
            this.providerName = providerName;
        }

        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return EventHubCheckpointer.Create(options, providerName, partition, this.clusterOptions.ServiceId.ToString(), loggerFactory);
        }

        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            var options = services.GetOptionsByName<AzureTableStreamCheckpointerOptions>(providerName);
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(providerName);
            return ActivatorUtilities.CreateInstance<EventHubCheckpointerFactory>(services, providerName, options, clusterOptions);
        }
    }

    /// <summary>
    /// This class stores EventHub partition checkpointer information (a partition offset) in azure table storage.
    /// </summary>
    public class EventHubCheckpointer : IStreamQueueCheckpointer<string>
    {
        private readonly AzureTableDataManager<EventHubPartitionCheckpointEntity> dataManager;
        private readonly TimeSpan persistInterval;
        private readonly ILogger logger;

        private EventHubPartitionCheckpointEntity entity;
        private Task inProgressSave;
        private DateTime? throttleSavesUntilUtc;

        /// <summary>
        /// Indicates if a checkpoint exists
        /// </summary>
        public bool CheckpointExists => entity != null && entity.Offset != EventHubConstants.StartOfStream;

        /// <summary>
        /// Factory function that creates and initializes the checkpointer
        /// </summary>
        /// <param name="options"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="partition"></param>
        /// <param name="serviceId"></param>
        /// <param name="loggerFactory"></param>
        /// <returns></returns>
        public static async Task<IStreamQueueCheckpointer<string>> Create(AzureTableStreamCheckpointerOptions options, string streamProviderName, string partition, string serviceId, ILoggerFactory loggerFactory)
        {
            var checkpointer = new EventHubCheckpointer(options, streamProviderName, partition, serviceId, loggerFactory);
            await checkpointer.Initialize();
            return checkpointer;
        }

        private EventHubCheckpointer(AzureTableStreamCheckpointerOptions options, string streamProviderName, string partition, string serviceId, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException(nameof(streamProviderName));
            }
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new ArgumentNullException(nameof(partition));
            }
            this.logger = loggerFactory.CreateLogger<EventHubCheckpointer>();
            this.logger.LogInformation(
                "Creating EventHub checkpointer for partition {Partition} of stream provider {StreamProviderName} with serviceId {ServiceId}.",
                partition,
                streamProviderName,
                serviceId);
            persistInterval = options.PersistInterval;
            dataManager = new AzureTableDataManager<EventHubPartitionCheckpointEntity>(
                options,
                loggerFactory.CreateLogger<EventHubPartitionCheckpointEntity>());
            entity = EventHubPartitionCheckpointEntity.Create(streamProviderName, serviceId, partition);
        }

        private Task Initialize()
        {
            return dataManager.InitTableAsync();
        }

        /// <summary>
        /// Loads a checkpoint
        /// </summary>
        /// <returns></returns>
        public async Task<string> Load()
        {
            var results = await dataManager.ReadSingleTableEntryAsync(entity.PartitionKey, entity.RowKey);
            if (results.Entity != null)
            {
                entity = results.Entity;
            }

            return entity.Offset;
        }

        /// <summary>
        /// Updates the checkpoint.  This is a best effort.  It does not always update the checkpoint.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="utcNow"></param>
        public void Update(string offset, DateTime utcNow)
        {
            // if offset has not changed, do nothing
            if (string.Compare(entity.Offset, offset, StringComparison.Ordinal) == 0)
            {
                return;
            }

            // if we've saved before but it's not time for another save or the last save operation has not completed, do nothing
            if (throttleSavesUntilUtc.HasValue && (throttleSavesUntilUtc.Value > utcNow || !inProgressSave.IsCompleted))
            {
                return;
            }

            entity.Offset = offset;
            throttleSavesUntilUtc = utcNow + persistInterval;
            inProgressSave = dataManager.UpsertTableEntryAsync(entity);
            inProgressSave.Ignore();
        }
    }
}
