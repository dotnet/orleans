
using System;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Orleans.AzureUtils;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
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

        public bool CheckpointExists { get { return entity != null && entity.Offset != EventHubConsumerGroup.StartOfStream; } }

        public static async Task<IStreamQueueCheckpointer<string>> Create(ICheckpointerSettings settings, string streamProviderName, string partition)
        {
            var checkpointer = new EventHubCheckpointer(settings, streamProviderName, partition);
            await checkpointer.Initialize();
            return checkpointer;
        }

        private EventHubCheckpointer(ICheckpointerSettings settings, string streamProviderName, string partition)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new ArgumentNullException("partition");
            }
            persistInterval = settings.PersistInterval;
            dataManager = new AzureTableDataManager<EventHubPartitionCheckpointEntity>(settings.TableName, settings.DataConnectionString);
            entity = EventHubPartitionCheckpointEntity.Create(streamProviderName, settings.CheckpointNamespace, partition);
        }

        private Task Initialize()
        {
            return dataManager.InitTableAsync();
        }

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

        public void Update(string offset, DateTime utcNow)
        {
            // if offset has not changed, do nothing
            if (string.Compare(entity.Offset, offset, StringComparison.InvariantCulture)==0)
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
