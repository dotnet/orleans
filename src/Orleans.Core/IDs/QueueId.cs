using System;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of a durable queue.
    /// Used by Orlens streaming extensions.
    /// </summary>
    [Serializable]
    [Immutable]
    public class QueueId : IRingIdentifier<QueueId>, IEquatable<QueueId>, IComparable<QueueId>
    {
        private static readonly Lazy<Interner<QueueId, QueueId>> queueIdInternCache = new Lazy<Interner<QueueId, QueueId>>(
                    () => new Interner<QueueId, QueueId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

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
            return FindOrCreateQueueId(queueName, queueId, hash);
        }

        private static QueueId FindOrCreateQueueId(string queuePrefix, uint id, uint hash)
        {
            var key = new QueueId(queuePrefix, id, hash);
            return queueIdInternCache.Value.FindOrCreate(key, k => k);
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

        #region IComparable<QueueId> Members

        public int CompareTo(QueueId other)
        {
            int cmp = queueId.CompareTo(other.queueId);
            if (cmp != 0) return cmp;

            cmp = String.Compare(queueNamePrefix, other.queueNamePrefix, StringComparison.Ordinal);
            if (cmp != 0) return cmp;
                
            return uniformHashCache.CompareTo(other.uniformHashCache);
        }

        #endregion

        #region IEquatable<QueueId> Members

        public virtual bool Equals(QueueId other)
        {
            return other != null 
                && queueId == other.queueId 
                && String.Equals(queueNamePrefix, other.queueNamePrefix, StringComparison.Ordinal) 
                && uniformHashCache == other.uniformHashCache;
        }

        #endregion

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
            return String.Format("{0}-{1}", (queueNamePrefix !=null ? queueNamePrefix.ToLower() : String.Empty), queueId.ToString());
        }

        public string ToStringWithHashCode()
        {
            return String.Format("{0}-0x{1, 8:X8}", this.ToString(), this.GetUniformHashCode());
        }
    }
}
