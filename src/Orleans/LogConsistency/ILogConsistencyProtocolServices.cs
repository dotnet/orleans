
using System;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.MultiCluster;
using Orleans.GrainDirectory;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Functionality for use by log view adaptors that use custom consistency or replication protocols.
    /// Abstracts communication between replicas of the log-consistent grain in different clusters.
    /// </summary>
    public interface ILogConsistencyProtocolServices
    {
        /// <summary>
        /// Send a message to a remote cluster.
        /// </summary>
        /// <param name="payload">the message</param>
        /// <param name="clusterId">the destination cluster id</param>
        /// <returns></returns>
        Task<ILogConsistencyProtocolMessage> SendMessage(ILogConsistencyProtocolMessage payload, string clusterId);


        /// <summary>
        /// The untyped reference for this grain.
        /// </summary>
        GrainReference GrainReference { get;  }

        
        /// <summary>
        /// The multicluster registration strategy for this grain.
        /// </summary>
        IMultiClusterRegistrationStrategy RegistrationStrategy { get; }


        /// <summary>
        /// Whether this cluster is running in a multi-cluster network.
        /// </summary>
        /// <returns></returns>
        bool MultiClusterEnabled { get; }

        // If there is no multi-cluster network,
        // we use a default multi-cluster configuration containing just one cluster (this one), named "I"
        // this is more convenient and consistent than returning null or invalid,
        // since it means the log-consistency providers do the right thing (run like a single-cluster configuration)

        /// <summary>
        /// The id of this cluster. Returns "I" if no multi-cluster network is present.
        /// </summary>
        /// <returns></returns>
        string MyClusterId { get; }

    
        /// <summary>
        /// The current multicluster configuration of this silo 
        /// (as injected by the administrator) or null if none.
        /// </summary>
        MultiClusterConfiguration MultiClusterConfiguration { get; }

        /// <summary>
        /// List of all clusters that currently appear to have at least one active
        /// gateway reporting to the multi-cluster network. 
        /// There are no guarantees that this membership view is complete or consistent.
        /// If there is no multi-cluster network, returns a list containing the single element "I".
        /// </summary>
        /// <returns></returns>
        IEnumerable<string>  ActiveClusters { get; }


        void SubscribeToMultiClusterConfigurationChanges();

        void UnsubscribeFromMultiClusterConfigurationChanges();


        #region Logging Functionality

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
        void Log(Severity severity, string format, params object[] args);

        #endregion
    }



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
