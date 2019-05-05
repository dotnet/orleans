using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    /// <summary>
    /// Default RabbitMQ data comparer which implements comparisons against <see cref="RabbitMQMessage"/>.
    /// </summary>
    public class RabbitMQDataComparer : ICacheDataComparer<CachedRabbitMQMessage>
    {
        /// <summary>
        /// Compare a cached message with a sequence token to determine if it message is before or after the token
        /// </summary>
        public int Compare(CachedRabbitMQMessage cachedMessage, StreamSequenceToken streamToken)
        {
            var realToken = (EventSequenceToken)streamToken;
            // cast to ulong so we can keep the sequence numbers sane.
            return (int)(cachedMessage.SequenceNumber - (ulong)realToken.SequenceNumber);
        }

        /// <summary>
        /// Checks to see if the cached message is part of the provided stream
        /// </summary>
        public bool Equals(CachedRabbitMQMessage cachedMessage, IStreamIdentity streamIdentity)
        {
            int result = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
            if (result != 0)
            {
                return false;
            } else
            {
                return true;
            }
        }
    }
}
