using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that a gateway silo is currently in overloaded / load shedding state 
    /// and is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is usually a transient condition.
    /// The message is likely to be accepted by this or another gateway if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class GatewayTooBusyException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayTooBusyException"/> class.
        /// </summary>
        public GatewayTooBusyException()
            : base("Gateway too busy")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayTooBusyException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public GatewayTooBusyException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayTooBusyException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public GatewayTooBusyException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayTooBusyException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        protected GatewayTooBusyException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

