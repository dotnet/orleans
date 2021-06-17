using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{

    /// <summary>
    /// Signifies that an operation was attempted on an invalid SchedulingContext.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    internal sealed class InvalidSchedulingContextException : OrleansException
    {
        public InvalidSchedulingContextException(string msg) : base(msg) { }
        public InvalidSchedulingContextException(string message, Exception innerException) : base(message, innerException) { }

        private InvalidSchedulingContextException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}

