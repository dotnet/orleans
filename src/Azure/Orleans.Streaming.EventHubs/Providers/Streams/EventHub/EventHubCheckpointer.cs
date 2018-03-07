using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Streaming.EventHubs;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.ServiceBus.Providers
{
    public class EventHubCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private IServiceProvider services;
        private string providerName;
        public EventHubCheckpointerFactory(string providerName, IServiceProvider services)
        {
            this.services = services;
            this.providerName = providerName;
        }

        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            var options = services.GetOptionsByName<EventHubCheckpointerOptions>(providerName);
            return EventHubCheckpointer.Create(options, providerName, partition, services.GetService<ILoggerFactory>());
        }
    }

    /// <summary>
    /// This class stores EventHub partition checkpointer information (a partition offset) in azure table storage.
    /// </summary>
    public class EventHubCheckpointer : IStreamQueueCheckpointer<string>
    {
        private readonly AzureTableDataManager<EventHubPartitionCheckpointEntity> dataManager;
        private readonly TimeSpan persistInterval;

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
        /// <param name="settings"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="partition"></param>
        /// <param name="loggerFactory"></param>
        /// <returns></returns>
        public static async Task<IStreamQueueCheckpointer<string>> Create(EventHubCheckpointerOptions options, string streamProviderName, string partition, ILoggerFactory loggerFactory)
        {
            var checkpointer = new EventHubCheckpointer(options, streamProviderName, partition, loggerFactory);
            await checkpointer.Initialize();
            return checkpointer;
        }

        private EventHubCheckpointer(EventHubCheckpointerOptions options, string streamProviderName, string partition, ILoggerFactory loggerFactory)
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
            persistInterval = options.CheckpointPersistInterval;
            dataManager = new AzureTableDataManager<EventHubPartitionCheckpointEntity>(options.CheckpointTableName, options.CheckpointConnectionString, loggerFactory);
            entity = EventHubPartitionCheckpointEntity.Create(streamProviderName, options.CheckpointNamespace, partition);
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
            Tuple<EventHubPartitionCheckpointEntity, string> results =
                await dataManager.ReadSingleTableEntryAsync(entity.PartitionKey, entity.RowKey);
            if (results != null)
            {
                entity = results.Item1;
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
