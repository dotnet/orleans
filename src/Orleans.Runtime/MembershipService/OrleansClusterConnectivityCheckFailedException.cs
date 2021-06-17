using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.MembershipService
{
    [Serializable]
    [GenerateSerializer]
    public class OrleansClusterConnectivityCheckFailedException : OrleansException
    {
        public OrleansClusterConnectivityCheckFailedException() : base("Failed to verify connectivity with active cluster nodes.") { }

        public OrleansClusterConnectivityCheckFailedException(string message) : base(message) { }

        public OrleansClusterConnectivityCheckFailedException(string message, Exception innerException) : base(message, innerException) { }

        public OrleansClusterConnectivityCheckFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
