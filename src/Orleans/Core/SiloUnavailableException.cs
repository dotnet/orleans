using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that an request was cancelled due to target silo unavailability.
    /// </summary>
    [Serializable]
    public class SiloUnavailableException : OrleansException
    {
        public SiloUnavailableException() : base("SiloUnavailableException") { }
        public SiloUnavailableException(string msg) : base(msg) { }
        public SiloUnavailableException(string message, Exception innerException) : base(message, innerException) { }

#if !NETSTANDARD
        protected SiloUnavailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }
}