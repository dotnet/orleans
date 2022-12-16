using System;
using System.Runtime.Serialization;

namespace Orleans.Persistence.Redis
{
    /// <summary>
    /// Exception for throwing from Redis grain storage.
    /// </summary>
    [GenerateSerializer]
    public class RedisStorageException : Exception
    {
        /// <summary>
        /// Initializes a new instance of <see cref="RedisStorageException"/>.
        /// </summary>
        public RedisStorageException()
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="RedisStorageException"/>.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public RedisStorageException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="RedisStorageException"/>.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        public RedisStorageException(string message, Exception inner) : base(message, inner)
        {
        }

        /// <inheritdoc />
        protected RedisStorageException(
            SerializationInfo info,
            StreamingContext context) : base(info, context)
        {
        }
    }
}