
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    public class EventHubQueueMapper : HashRingBasedStreamQueueMapper, IEventHubQueueMapper
    {
        private readonly Dictionary<QueueId, string> partitionDictionary = new Dictionary<QueueId, string>();

        public EventHubQueueMapper(string[] partitionIds, string queueNamePrefix)
            : base(partitionIds.Length, queueNamePrefix)
        {
            QueueId[] queues = GetAllQueues().ToArray();
            if (queues.Length != partitionIds.Length)
            {
                throw new ArgumentOutOfRangeException("partitionIds", "partitons and Queues do not line up");
            }
            for (int i = 0; i < queues.Length; i++)
            {
                partitionDictionary.Add(queues[i], partitionIds[i]);
            }
        }

        public string QueueToPartition(QueueId queue)
        {
            if (queue == null)
            {
                throw new ArgumentNullException("queue");
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
