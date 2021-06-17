using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that an Orleans message was rejected.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansMessageRejectionException : OrleansException
    {
        internal OrleansMessageRejectionException(string message)
            : base(message)
        {
        }

        internal OrleansMessageRejectionException(string message,
            Exception innerException) : base(message, innerException)
        {
        }

        protected OrleansMessageRejectionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

