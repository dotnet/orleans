using System;
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
        private readonly HashRing hashRing;

        /// <summary>
        /// Initializes a new instance of the <see cref="HashRingBasedStreamQueueMapper"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="queueNamePrefix">The queue name prefix.</param>
        public HashRingBasedStreamQueueMapper(HashRingStreamQueueMapperOptions options, string queueNamePrefix) : this(options.TotalQueueCount, queueNamePrefix) { }

        internal HashRingBasedStreamQueueMapper(int numQueues, string queueNamePrefix)
        {
            if (numQueues < 1) throw new ArgumentException("TotalQueueCount must be at least 1");
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

            this.hashRing = new(queueIds);
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
        public IEnumerable<QueueId> GetAllQueues() => hashRing.GetAllRingMembers();

        /// <inheritdoc/>
        public QueueId GetQueueForStream(StreamId streamId) => hashRing.CalculateResponsible((uint)streamId.GetHashCode());

        /// <inheritdoc/>
        public override string ToString() => hashRing.ToString();
    }

    /// <summary>
    /// Queue mapper that tracks which partition was mapped to which QueueId
    /// </summary>
    public sealed class HashRingBasedPartitionedStreamQueueMapper : HashRingBasedStreamQueueMapper
    {
        private readonly Dictionary<QueueId, string> _partitions;

        /// <summary>
        /// Queue mapper that tracks which partition was mapped to which QueueId
        /// </summary>
        /// <param name="partitionIds">List of partitions</param>
        /// <param name="queueNamePrefix">Prefix for QueueIds.  Must be unique per stream provider</param>
        public HashRingBasedPartitionedStreamQueueMapper(IReadOnlyList<string> partitionIds, string queueNamePrefix)
            : base(partitionIds.Count, queueNamePrefix)
        {
            var queues = (QueueId[])GetAllQueues();
            _partitions = new(queues.Length);
            for (var i = 0; i < queues.Length; i++)
                _partitions.Add(queues[i], partitionIds[i]);
        }

        /// <summary>
        /// Gets the partition by QueueId
        /// </summary>
        /// <param name="queue"></param>
        /// <returns></returns>
        public string QueueToPartition(QueueId queue) => _partitions.TryGetValue(queue, out var p) ? p : throw new ArgumentOutOfRangeException($"Queue {queue:H}");
    }
}
