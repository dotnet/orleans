﻿using System;
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
        private ILoggerFactory loggerFactory;
        private string providerName;
        private AzureTableStreamCheckpointerOptions options;
        public EventHubCheckpointerFactory(string providerName, AzureTableStreamCheckpointerOptions options, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.loggerFactory = loggerFactory;
            this.providerName = providerName;
        }

        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return EventHubCheckpointer.Create(options, providerName, partition, loggerFactory);
        }

        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            var options = services.GetOptionsByName<AzureTableStreamCheckpointerOptions>(providerName);
            return ActivatorUtilities.CreateInstance<EventHubCheckpointerFactory>(services, providerName, options);
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
        public static async Task<IStreamQueueCheckpointer<string>> Create(AzureTableStreamCheckpointerOptions options, string streamProviderName, string partition, ILoggerFactory loggerFactory)
        {
            var checkpointer = new EventHubCheckpointer(options, streamProviderName, partition, loggerFactory);
            await checkpointer.Initialize();
            return checkpointer;
        }

        private EventHubCheckpointer(AzureTableStreamCheckpointerOptions options, string streamProviderName, string partition, ILoggerFactory loggerFactory)
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
            persistInterval = options.PersistInterval;
            dataManager = new AzureTableDataManager<EventHubPartitionCheckpointEntity>(options.TableName, options.ConnectionString, loggerFactory);
            entity = EventHubPartitionCheckpointEntity.Create(streamProviderName, options.Namespace, partition);
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
