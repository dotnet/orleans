using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Exception indicates that the requested data is not available.
    /// </summary>
    [Serializable]
    public class DataNotAvailableException : OrleansException
    {
        public DataNotAvailableException() : this("Data not found") { }
        public DataNotAvailableException(string message) : base(message) { }
        public DataNotAvailableException(string message, Exception inner) : base(message, inner) { }

#if !NETSTANDARD
        public DataNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }
}
