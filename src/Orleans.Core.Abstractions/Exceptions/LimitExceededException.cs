using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that a silo is in an overloaded state where some 
    /// runtime limit setting is currently being exceeded, 
    /// and so that silo is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is often a transient condition.
    /// The message is likely to be accepted by this or another silo if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class LimitExceededException : OrleansException
    {
        public LimitExceededException() : base("Limit exceeded") { }

        public LimitExceededException(string message) : base(message) { }

        public LimitExceededException(string message, Exception innerException) : base(message, innerException) { }

        public LimitExceededException(string limitName, int current, int threshold, object extraInfo) 
            : base(string.Format("Limit exceeded {0} Current={1} Threshold={2} {3}", limitName, current, threshold, extraInfo)) { }

        protected LimitExceededException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

