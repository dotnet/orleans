using System;
using System.Runtime.Serialization;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Functionality for use by log view adaptors that use custom consistency or replication protocols.
    /// Abstracts communication between replicas of the log-consistent grain in different clusters.
    /// </summary>
    public interface ILogConsistencyProtocolServices
    {
        /// <summary>
        /// The ID for this grain.
        /// </summary>
        GrainId GrainId { get;  }

        /// <summary>
        /// Copies the provided argument.
        /// </summary>
        T DeepCopy<T>(T value);

        /// <summary>
        /// The id of this cluster. Returns "I" if no multi-cluster network is present.
        /// </summary>
        /// <returns></returns>
        string MyClusterId { get; }

        /// <summary>
        /// Log an error that occurred in a log-consistency protocol.
        /// </summary>
        void ProtocolError(string msg, bool throwexception);

        /// <summary>
        /// Log an exception that was caught in the log-consistency protocol.
        /// </summary> 
        void CaughtException(string where, Exception e);

        /// <summary>
        /// Log an exception that occurred in user code, for some callback
        /// </summary>
        /// <param name="callback">The name of the callback</param>
        /// <param name="where">The context from which the callback was called</param>
        /// <param name="e">The caught exception</param>
        void CaughtUserCodeException(string callback, string where, Exception e);

        /// <summary> Output the specified message at the specified log level. </summary>
        void Log(LogLevel level, string format, params object[] args);
    }



    /// <summary>
    /// Exception thrown by protocol messaging layer.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
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

        protected ProtocolTransportException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }

        public override string ToString()
        {
            if (InnerException != null)
                return $"ProtocolTransportException: {InnerException}";
            else
                return Message;
        }
    }
}
