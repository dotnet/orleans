using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.Messaging
{
    public class ConnectionFailedException : OrleansException
    {
        public ConnectionFailedException()
        {
        }

        public ConnectionFailedException(string message) : base(message)
        {
        }

        public ConnectionFailedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected ConnectionFailedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
