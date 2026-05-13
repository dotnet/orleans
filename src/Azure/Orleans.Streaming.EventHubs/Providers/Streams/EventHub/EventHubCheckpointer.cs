using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Overrides;

#nullable disable
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
    public partial class EventHubCheckpointer : IStreamQueueCheckpointer<string>
    {
        private readonly AzureTableDataManager<EventHubPartitionCheckpointEntity> dataManager;
        private readonly TimeSpan persistInterval;
        private readonly ILogger logger;

        private EventHubPartitionCheckpointEntity entity;
        private Task inProgressSave = Task.CompletedTask;
        private DateTime? throttleSavesUntilUtc;
        private string latestOffset;

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
            LogCreatingEventHubCheckpointer(partition, streamProviderName, serviceId);
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

            latestOffset = entity.Offset;
            return entity.Offset;
        }

        /// <summary>
        /// Updates the checkpoint.  This is a best effort.  It does not always update the checkpoint.
        /// The latest offset is always tracked in memory so that <see cref="FlushAsync"/> can persist it on shutdown.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="utcNow"></param>
        public void Update(string offset, DateTime utcNow)
        {
            // if offset has not changed, do nothing
            if (string.Compare(latestOffset, offset, StringComparison.Ordinal) == 0)
            {
                return;
            }

            // Only move the in-memory offset forward (Event Hub offsets are numeric strings).
            // This prevents a purge-based checkpoint from regressing past a read-based checkpoint.
            if (long.TryParse(offset, out var newOffset)
                && long.TryParse(latestOffset, out var currentOffset)
                && newOffset <= currentOffset)
            {
                return;
            }

            // Always track the latest offset in memory so FlushAsync can persist it.
            latestOffset = offset;

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

        /// <summary>
        /// Flushes any pending checkpoint to persistent storage.
        /// Awaits any in-progress save, then persists the latest offset if it has advanced beyond the last saved value.
        /// </summary>
        public async Task FlushAsync()
        {
            await inProgressSave;
            if (string.Compare(entity.Offset, latestOffset, StringComparison.Ordinal) != 0)
            {
                entity.Offset = latestOffset;
                inProgressSave = dataManager.UpsertTableEntryAsync(entity);
                await inProgressSave;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Creating EventHub checkpointer for partition {Partition} of stream provider {StreamProviderName} with serviceId {ServiceId}."
        )]
        private partial void LogCreatingEventHubCheckpointer(string partition, string streamProviderName, string serviceId);
    }
}
