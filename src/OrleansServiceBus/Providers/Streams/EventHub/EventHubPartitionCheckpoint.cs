
using System;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Orleans.AzureUtils;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// This class stores EventHub partition checkpoint information (a partition offset) in azure table storage.
    /// TODO: Use dependency injection to fill this behavior.
    /// This class stores data into table storage, which will not be ideal for all users.  Further, it introduces
    ///   a dependency on azure storage to this assembly.  Ideally this should be an injectable behavior, rather
    ///   than a concrete class.
    /// </summary>
    internal class EventHubPartitionCheckpoint
    {
        private readonly AzureTableDataManager<EventHubPartitionCheckpointEntity> dataManager;
        private readonly TimeSpan persistInterval;

        private EventHubPartitionCheckpointEntity entity;
        private Task inProgressSave;
        private DateTime? throttleSavesUntilUtc;

        public bool Exists { get { return entity != null && entity.Offset != EventHubConsumerGroup.StartOfStream; } }

        public static async Task<EventHubPartitionCheckpoint> Create(ICheckpointSettings settings, string streamProviderName, string partition)
        {
            var checkpoint = new EventHubPartitionCheckpoint(settings, streamProviderName, partition);
            await checkpoint.Initialize();
            return checkpoint;
        }

        private EventHubPartitionCheckpoint(ICheckpointSettings settings, string streamProviderName, string partition)
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
