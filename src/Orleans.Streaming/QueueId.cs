using System;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of a durable queue.
    /// Used by Orleans streaming extensions.
    /// </summary>
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    public sealed class QueueId : IRingIdentifier<QueueId>, IEquatable<QueueId>, IComparable<QueueId>
    {
        // TODO: Need to integrate with Orleans serializer to really use Interner.        
        private static readonly Interner<QueueId, QueueId> queueIdInternCache = new Interner<QueueId, QueueId>(InternerConstants.SIZE_LARGE);
        [Id(1)]
        private readonly string queueNamePrefix;
        [Id(2)]
        private readonly uint queueId;
        [Id(3)]
        private readonly uint uniformHashCache;

        /// <summary>
        /// Initializes a new instance of the <see cref="QueueId"/> class.
        /// </summary>
        /// <param name="queuePrefix">The queue prefix.</param>
        /// <param name="id">The identifier.</param>
        /// <param name="hash">The hash.</param>
        private QueueId(string queuePrefix, uint id, uint hash)
        {
            queueNamePrefix = queuePrefix;
            queueId = id;
            uniformHashCache = hash;
        }

        /// <summary>
        /// Gets the queue identifier.
        /// </summary>
        /// <param name="queueName">Name of the queue.</param>
        /// <param name="queueId">The queue identifier.</param>
        /// <param name="hash">The hash.</param>
        /// <returns>The queue identifier.</returns>
        public static QueueId GetQueueId(string queueName, uint queueId, uint hash)
        {
            var key = new QueueId(queueName, queueId, hash);
            return queueIdInternCache.Intern(key, key);
        }

        /// <summary>
        /// Gets the queue name prefix.
        /// </summary>
        /// <returns>The queue name prefix.</returns>
        public string GetStringNamePrefix()
        {
            return queueNamePrefix;
        }

        /// <summary>
        /// Gets the numeric identifier.
        /// </summary>
        /// <returns>The numeric identifier.</returns>
        public uint GetNumericId()
        {
            return queueId;
        }

        /// <inheritdoc/>
        public uint GetUniformHashCode()
        {
            return uniformHashCache;
        }

        /// <inheritdoc/>
        public int CompareTo(QueueId other)
        {
            if (queueId != other.queueId)
                return queueId.CompareTo(other.queueId);

            var cmp = string.CompareOrdinal(queueNamePrefix, other.queueNamePrefix);
            if (cmp != 0) return cmp;
                
            return uniformHashCache.CompareTo(other.uniformHashCache);
        }

        /// <inheritdoc/>
        public bool Equals(QueueId other)
        {
            return other != null 
                && queueId == other.queueId 
                && queueNamePrefix == other.queueNamePrefix
                && uniformHashCache == other.uniformHashCache;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return this.Equals(obj as QueueId);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return (int)queueId ^ (queueNamePrefix !=null ? queueNamePrefix.GetHashCode() : 0) ^ (int)uniformHashCache;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"{queueNamePrefix?.ToLowerInvariant()}-{queueId.ToString()}";
        }

        /// <summary>
        /// Returns a string representation of this instance which includes its uniform hash code.
        /// </summary>
        /// <returns>A string representation of this instance which includes its uniform hash code.</returns>
        public string ToStringWithHashCode()
        {
            return $"{queueNamePrefix?.ToLowerInvariant()}-{queueId.ToString()}-0x{GetUniformHashCode().ToString("X8")}";
        }
    }
}
