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

        public DataNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class CacheFullException : OrleansException
    {
        public CacheFullException() : this("Queue message cache is full") { }
        public CacheFullException(string message) : base(message) { }
        public CacheFullException(string message, Exception inner) : base(message, inner) { }

        public CacheFullException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
