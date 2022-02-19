using System.Collections.Generic;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// A <see cref="IConsistentRingStreamQueueMapper"/> and hence <see cref="IStreamQueueMapper"/> which balances queues by mapping them onto a hash ring consisting of silos.
    /// </summary>
    public class HashRingBasedStreamQueueMapper : IConsistentRingStreamQueueMapper
    {
        private readonly HashRing<QueueId> hashRing;

        /// <summary>
        /// Initializes a new instance of the <see cref="HashRingBasedStreamQueueMapper"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="queueNamePrefix">The queue name prefix.</param>
        public HashRingBasedStreamQueueMapper(HashRingStreamQueueMapperOptions options, string queueNamePrefix)
        {
            var numQueues = options.TotalQueueCount;
            var queueIds = new QueueId[numQueues];
            if (numQueues == 1)
            {
                queueIds[0] = QueueId.GetQueueId(queueNamePrefix, 0, 0);
            }
            else
            {
                uint portion = checked((uint)(RangeFactory.RING_SIZE / numQueues + 1));
                for (uint i = 0; i < numQueues; i++)
                {
                    uint uniformHashCode = checked(portion * i);
                    queueIds[i] = QueueId.GetQueueId(queueNamePrefix, i, uniformHashCode);
                }
            }

            this.hashRing = new HashRing<QueueId>(queueIds);
        }

        /// <inheritdoc/>
        public IEnumerable<QueueId> GetQueuesForRange(IRingRange range)
        {
            var ls = new List<QueueId>();
            foreach (QueueId queueId in hashRing.GetAllRingMembers())
            {
                if (range.InRange(queueId.GetUniformHashCode()))
                {
                    ls.Add(queueId);
                }
            }

            return ls;
        }

        /// <inheritdoc/>
        public IEnumerable<QueueId> GetAllQueues()
        {
            return hashRing.GetAllRingMembers();
        }

        /// <inheritdoc/>
        public QueueId GetQueueForStream(StreamId streamId)
        {
            return hashRing.CalculateResponsible((uint)streamId.GetHashCode());
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return hashRing.ToString();
        }
    }
}
