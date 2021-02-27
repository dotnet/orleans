using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that an request was cancelled due to target silo unavailability.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class SiloUnavailableException : OrleansMessageRejectionException
    {
        public SiloUnavailableException() : base("SiloUnavailableException") { }
        public SiloUnavailableException(string msg) : base(msg) { }
        public SiloUnavailableException(string message, Exception innerException) : base(message, innerException) { }

        protected SiloUnavailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

