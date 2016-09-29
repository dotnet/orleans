
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Queue mapper that tracks which EventHub partition was mapped to which queueId
    /// </summary>
    public class EventHubQueueMapper : HashRingBasedStreamQueueMapper, IEventHubQueueMapper
    {
        private readonly Dictionary<QueueId, string> partitionDictionary = new Dictionary<QueueId, string>();

        /// <summary>
        /// Queue mapper that tracks which EventHub partition was mapped to which queueId
        /// </summary>
        /// <param name="partitionIds">List of EventHubPartitions</param>
        /// <param name="queueNamePrefix">Prefix for queueIds.  Must be unique per stream provider</param>
        public EventHubQueueMapper(string[] partitionIds, string queueNamePrefix)
            : base(partitionIds.Length, queueNamePrefix)
        {
            QueueId[] queues = GetAllQueues().ToArray();
            if (queues.Length != partitionIds.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(partitionIds), "partitons and Queues do not line up");
            }
            for (int i = 0; i < queues.Length; i++)
            {
                partitionDictionary.Add(queues[i], partitionIds[i]);
            }
        }

        /// <summary>
        /// Gets the EventHub partition by QueueId
        /// </summary>
        /// <param name="queue"></param>
        /// <returns></returns>
        public string QueueToPartition(QueueId queue)
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
