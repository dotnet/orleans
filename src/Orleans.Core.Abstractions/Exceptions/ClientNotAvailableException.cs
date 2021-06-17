using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that a client is not longer reachable.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class ClientNotAvailableException : OrleansException
    {
        internal ClientNotAvailableException(GrainId clientId) : base("No activation for client " + clientId.ToString()) { }
        internal ClientNotAvailableException(string msg) : base(msg) { }
        internal ClientNotAvailableException(string message, Exception innerException) : base(message, innerException) { }

        protected ClientNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

