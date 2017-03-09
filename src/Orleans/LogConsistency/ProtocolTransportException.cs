using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Exception thrown by protocol messaging layer.
    /// </summary>
    [Serializable]
    public class ProtocolTransportException : OrleansException
    {
        public ProtocolTransportException()
        { }
        public ProtocolTransportException(string msg)
            : base(msg)
        { }
        public ProtocolTransportException(string msg, Exception exc)
            : base(msg, exc)
        { }

#if !NETSTANDARD
        protected ProtocolTransportException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif

        public override string ToString()
        {
            if (InnerException != null)
                return $"ProtocolTransportException: {InnerException}";
            else
                return Message;
        }
    }
}