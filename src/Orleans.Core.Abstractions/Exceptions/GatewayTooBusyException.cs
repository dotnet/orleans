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
        public GatewayTooBusyException() : base("Gateway too busy") { }

        public GatewayTooBusyException(string message) : base(message) { }

        public GatewayTooBusyException(string message, Exception innerException) : base(message, innerException) { }

        protected GatewayTooBusyException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

