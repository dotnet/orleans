using System;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of a durable queue.
    /// Used by Orleans streaming extensions.
    /// </summary>
    [Serializable]
    [Immutable]
    public sealed class QueueId : IRingIdentifier<QueueId>, IEquatable<QueueId>, IComparable<QueueId>
    {
        private static readonly Interner<QueueId, QueueId> queueIdInternCache = new Interner<QueueId, QueueId>(InternerConstants.SIZE_LARGE);

        private readonly string queueNamePrefix;
        private readonly uint queueId;
        private readonly uint uniformHashCache;

        // TODO: Need to integrate with Orleans serializer to really use Interner.
        private QueueId(string queuePrefix, uint id, uint hash)
        {
            queueNamePrefix = queuePrefix;
            queueId = id;
            uniformHashCache = hash;
        }

        public static QueueId GetQueueId(string queueName, uint queueId, uint hash)
        {
            var key = new QueueId(queueName, queueId, hash);
            return queueIdInternCache.Intern(key, key);
        }

        public string GetStringNamePrefix()
        {
            return queueNamePrefix;
        }

        public uint GetNumericId()
        {
            return queueId;
        }

        public uint GetUniformHashCode()
        {
            return uniformHashCache;
        }

        public int CompareTo(QueueId other)
        {
            if (queueId != other.queueId)
                return queueId.CompareTo(other.queueId);

            var cmp = string.CompareOrdinal(queueNamePrefix, other.queueNamePrefix);
            if (cmp != 0) return cmp;
                
            return uniformHashCache.CompareTo(other.uniformHashCache);
        }

        public bool Equals(QueueId other)
        {
            return other != null 
                && queueId == other.queueId 
                && queueNamePrefix == other.queueNamePrefix
                && uniformHashCache == other.uniformHashCache;
        }

        public override bool Equals(object obj)
        {
            return this.Equals(obj as QueueId);
        }

        public override int GetHashCode()
        {
            return (int)queueId ^ (queueNamePrefix !=null ? queueNamePrefix.GetHashCode() : 0) ^ (int)uniformHashCache;
        }

        public override string ToString()
        {
            return $"{queueNamePrefix?.ToLowerInvariant()}-{queueId.ToString()}";
        }

        public string ToStringWithHashCode()
        {
            return $"{queueNamePrefix?.ToLowerInvariant()}-{queueId.ToString()}-0x{GetUniformHashCode().ToString("X8")}";
        }
    }
}
