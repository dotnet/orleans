using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that a client is not longer reachable.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class ClientNotAvailableException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClientNotAvailableException"/> class.
        /// </summary>
        /// <param name="clientId">
        /// The client id.
        /// </param>
        internal ClientNotAvailableException(GrainId clientId)
            : base($"No activation for client {clientId}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientNotAvailableException"/> class.
        /// </summary>
        /// <param name="message">
        /// The exception message.
        /// </param>
        internal ClientNotAvailableException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientNotAvailableException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        internal ClientNotAvailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientNotAvailableException"/> class.
        /// </summary>
        /// <param name="info">
        /// The info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        protected ClientNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

