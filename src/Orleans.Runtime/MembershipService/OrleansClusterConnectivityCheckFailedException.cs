using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Exception used to indicate that a cluster connectivity check failed.
    /// </summary>
    /// <seealso cref="Orleans.Runtime.OrleansException" />
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansClusterConnectivityCheckFailedException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansClusterConnectivityCheckFailedException"/> class.
        /// </summary>
        public OrleansClusterConnectivityCheckFailedException() : base("Failed to verify connectivity with active cluster nodes.") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansClusterConnectivityCheckFailedException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public OrleansClusterConnectivityCheckFailedException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansClusterConnectivityCheckFailedException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public OrleansClusterConnectivityCheckFailedException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansClusterConnectivityCheckFailedException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private OrleansClusterConnectivityCheckFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
