using System;
using Azure;
using Azure.Data.Tables;
using Orleans.Streaming.EventHubs;

namespace Orleans.Streams
{
    internal sealed class StreamQueueCheckpointEntity : ITableEntity
    {
        internal const string EventHubPartitionKeyPrefix = "EventHubCheckpoints_";

        public string Offset { get; set; } = string.Empty;
        public string PartitionKey { get; set; } = null!;
        public string RowKey { get; set; } = null!;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public static StreamQueueCheckpointEntity Create(
            string partitionKeyPrefix,
            string streamProviderName,
            string serviceId,
            string partition)
        {
            return new StreamQueueCheckpointEntity
            {
                PartitionKey = AzureTableUtils.SanitizeTableProperty(
                    $"{partitionKeyPrefix}{streamProviderName}_{serviceId}"),
                RowKey = AzureTableUtils.SanitizeTableProperty($"partition_{partition}")
            };
        }
    }
}
