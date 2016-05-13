using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class HashRingBasedStreamQueueMapper : IConsistentRingStreamQueueMapper
    {
        private readonly int numQueues;
        private readonly HashRing<QueueId> hashRing;

        public HashRingBasedStreamQueueMapper(int nQueues, string queueNamePrefix)
        {
            numQueues = nQueues;
            var queueIds = new List<QueueId>(numQueues);
            if (nQueues == 1)
            {
                uint uniformHashCode = 0;
                queueIds.Add(QueueId.GetQueueId(queueNamePrefix, 0, uniformHashCode));
            }
            else
            {
                uint portion = checked((uint)(RangeFactory.RING_SIZE / numQueues + 1));
                for (uint i = 0; i < numQueues; i++)
                {
                    uint uniformHashCode = checked(portion * i);
                    queueIds.Add(QueueId.GetQueueId(queueNamePrefix, i, uniformHashCode));
                }
            }
            this.hashRing = new HashRing<QueueId>(queueIds);
        }

        public IEnumerable<QueueId> GetQueuesForRange(IRingRange range)
        {
            foreach (QueueId queueId in hashRing.GetAllRingMembers())
                if (range.InRange(queueId.GetUniformHashCode()))
                    yield return queueId;
        }

        public IEnumerable<QueueId> GetAllQueues()
        {
            return hashRing.GetAllRingMembers();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters")]
        public QueueId GetQueueForStream(Guid streamGuid, String streamNamespace)
        {
            return hashRing.CalculateResponsible(streamGuid);
        }

        public override string ToString()
        {
            return hashRing.ToString();
        }
    }
}
