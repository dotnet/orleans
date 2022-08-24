using System;

#nullable enable
namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of a durable queue.
    /// Used by Orleans streaming extensions.
    /// </summary>
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    public readonly struct QueueId : IEquatable<QueueId>, IComparable<QueueId>, ISpanFormattable
    {
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
            queueNamePrefix = queuePrefix ?? throw new ArgumentNullException(nameof(queuePrefix));
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
        public static QueueId GetQueueId(string queueName, uint queueId, uint hash) => new(queueName, queueId, hash);

        /// <summary>
        /// Gets the queue name prefix.
        /// </summary>
        /// <returns>The queue name prefix.</returns>
        public string GetStringNamePrefix() => queueNamePrefix;

        /// <summary>
        /// Gets the numeric identifier.
        /// </summary>
        /// <returns>The numeric identifier.</returns>
        public uint GetNumericId() => queueId;

        /// <inheritdoc/>
        public uint GetUniformHashCode() => uniformHashCache;

        /// <summary>
        /// Gets a value indicating whether the instance is the default instance.
        /// </summary>
        public bool IsDefault => queueNamePrefix is null;

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
        public bool Equals(QueueId other) => queueId == other.queueId && uniformHashCache == other.uniformHashCache && queueNamePrefix == other.queueNamePrefix;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is QueueId queueId && Equals(queueId);

        /// <inheritdoc/>
        public override int GetHashCode() => (int)queueId ^ (int)uniformHashCache ^ (queueNamePrefix?.GetHashCode() ?? 0);

        public static bool operator ==(QueueId left, QueueId right) => left.Equals(right);

        public static bool operator !=(QueueId left, QueueId right) => !(left == right);

        /// <inheritdoc/>
        public override string ToString() => $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var len = queueNamePrefix.AsSpan().ToLowerInvariant(destination);
            if (len >= 0 && destination[len..].TryWrite($"-{queueId}", out var len2))
            {
                len += len2;

                if (format.Length == 1 && format[0] == 'H')
                {
                    if (!destination[len..].TryWrite($"-0x{uniformHashCache:X8}", out len2))
                    {
                        charsWritten = 0;
                        return false;
                    }
                    len += len2;
                }

                charsWritten = len;
                return true;
            }

            charsWritten = 0;
            return false;
        }

        /// <summary>
        /// Returns a string representation of this instance which includes its uniform hash code.
        /// </summary>
        /// <returns>A string representation of this instance which includes its uniform hash code.</returns>
        public string ToStringWithHashCode() => $"{this:H}";
    }
}
