using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue
{
    public interface IAzureStreamQueueMapper : IStreamQueueMapper
    {
        /// <summary>
        /// Gets the Azure queue name by partition
        /// </summary>
        /// <param name="queue"></param>
        /// <returns></returns>
        string PartitionToAzureQueue(QueueId queue);
    }

    public class AzureStreamQueueMapper : HashRingBasedStreamQueueMapper, IAzureStreamQueueMapper
    {
        private readonly Dictionary<QueueId, string> partitionDictionary = new Dictionary<QueueId, string>();
        private static HashRingStreamQueueMapperOptions GetHashRingStreamQueueMapperOptions(List<string> azureQueueNames)
        {
            var options = new HashRingStreamQueueMapperOptions();
            options.TotalQueueCount = azureQueueNames.Count;
            return options;
        }
        /// <summary>
        /// Queue mapper that tracks which Azure queue was mapped to which queueId
        /// </summary>
        /// <param name="azureQueueNames">List of EventHubPartitions</param>
        /// <param name="queueNamePrefix">Prefix for queueIds.  Must be unique per stream provider</param>
        public AzureStreamQueueMapper(List<string> azureQueueNames, string queueNamePrefix)
            : base(GetHashRingStreamQueueMapperOptions(azureQueueNames), queueNamePrefix)
        {
            QueueId[] queues = GetAllQueues().ToArray();
            if (queues.Length != azureQueueNames.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(azureQueueNames), "Azure queue names and Queues do not line up");
            }
            for (int i = 0; i < queues.Length; i++)
            {
                partitionDictionary.Add(queues[i], azureQueueNames[i]);
            }
        }

        /// <summary>
        /// Gets the Azure queue by partition
        /// </summary>
        /// <param name="queue"></param>
        /// <returns></returns>
        public string PartitionToAzureQueue(QueueId queue)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            string partitionId;
            if (!partitionDictionary.TryGetValue(queue, out partitionId))
            {
                throw new ArgumentOutOfRangeException(string.Format(CultureInfo.InvariantCulture, "queue {0}", queue.ToStringWithHashCode()));
            }
            return partitionId;
        }
    }
}
