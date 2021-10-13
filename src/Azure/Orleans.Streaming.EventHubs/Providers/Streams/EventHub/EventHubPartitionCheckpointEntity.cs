using System;
using Azure;
using Azure.Data.Tables;
using Orleans.Streaming.EventHubs;

namespace Orleans.ServiceBus.Providers
{
    internal class EventHubPartitionCheckpointEntity : ITableEntity
    {
        public string Offset { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public EventHubPartitionCheckpointEntity()
        {
            Offset = EventHubConstants.StartOfStream;
        }

        public static EventHubPartitionCheckpointEntity Create(string streamProviderName, string serviceId, string partition)
        {
            return new EventHubPartitionCheckpointEntity
            {
                PartitionKey = MakePartitionKey(streamProviderName, serviceId),
                RowKey = MakeRowKey(partition)
            };
        }

        public static string MakePartitionKey(string streamProviderName, string checkpointNamespace)
        {
            string key = $"EventHubCheckpoints_{streamProviderName}_{checkpointNamespace}";
            return AzureTableUtils.SanitizeTableProperty(key);
        }

        public static string MakeRowKey(string partition)
        {
            string key = $"partition_{partition}";
            return AzureTableUtils.SanitizeTableProperty(key);
        }
    }
}
