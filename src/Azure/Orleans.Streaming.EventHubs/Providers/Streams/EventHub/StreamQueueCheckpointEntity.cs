using System;
using Azure;
using Azure.Data.Tables;
using Orleans.Streaming.EventHubs;

namespace Orleans.Streams
{
    internal sealed class StreamQueueCheckpointEntity : ITableEntity
    {
        public string Offset { get; set; } = string.Empty;
        public string PartitionKey { get; set; } = null!;
        public string RowKey { get; set; } = null!;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public static StreamQueueCheckpointEntity Create(string streamProviderName, string serviceId, string partition)
        {
            return new StreamQueueCheckpointEntity
            {
                // Retain the existing key format so that Event Hubs checkpoints remain compatible.
                PartitionKey = AzureTableUtils.SanitizeTableProperty(
                    $"EventHubCheckpoints_{streamProviderName}_{serviceId}"),
                RowKey = AzureTableUtils.SanitizeTableProperty($"partition_{partition}")
            };
        }
    }
}
