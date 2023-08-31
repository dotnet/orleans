using System;
using System.Runtime.Serialization;

namespace Orleans.Clustering.Redis
{
    /// <summary>
    /// Represents an exception which occurred in the Redis clustering.
    /// </summary>
    [Serializable]
    public class RedisClusteringException : Exception
    {
        /// <inheritdoc/>
        public RedisClusteringException() : base() { }

        /// <inheritdoc/>
        public RedisClusteringException(string message) : base(message) { }

        /// <inheritdoc/>
        public RedisClusteringException(string message, Exception innerException) : base(message, innerException) { }

        /// <inheritdoc/>
        protected RedisClusteringException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}