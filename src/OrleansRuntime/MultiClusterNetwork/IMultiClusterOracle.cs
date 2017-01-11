using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.MultiCluster;
using System;

namespace Orleans.Runtime.MultiClusterNetwork
{
    // Interface for local, per-silo authorative source of information about status of other silos.
    // A local interface for local communication between in-silo runtime components and this ISiloStatusOracle.
    internal interface IMultiClusterOracle
    {
        /// <summary>
        /// Starts this instance.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Start();

        /// <summary>
        /// Get the latest multicluster configuration.
        /// </summary>
        /// <returns>The current multicluster configuration, or null if there is none</returns>
        MultiClusterConfiguration GetMultiClusterConfiguration();

        /// <summary>
        /// Inject a multicluster configuration. For this to have any effect, the timestamp must be newer 
        /// than the latest configuation stored in the multicluster network.
        /// </summary>
        /// <returns>A task that completes once information has propagated to the multicluster channels</returns>
        Task InjectMultiClusterConfiguration(MultiClusterConfiguration configuration);

        /// <summary>
        /// Whether a gateway is functional (to the best knowledge of this node) 
        /// </summary>
        /// <param name="siloAddress">A gateway whose status we are interested in.</param>
        bool IsFunctionalClusterGateway(SiloAddress siloAddress);
    
        /// <summary>
        /// Contact all silos in all clusters, return silos that do not have the expected configuration.
        /// </summary>
        /// <returns>A dictionary containing silo addresses and the corresponding configuration for all non-matching configurations</returns>
        Task<List<SiloAddress>> FindLaggingSilos(MultiClusterConfiguration expected);
  
        /// <summary>
        /// Returns a list of cluster ids for active clusters based on what gateways we have stored in the table.
        /// </summary>
        /// <returns></returns>
        IEnumerable<string> GetActiveClusters();

        /// <summary>
        /// Returns the list of currently known multicluster gateways.
        /// </summary>
        /// <returns></returns>
        IEnumerable<GatewayEntry> GetGateways();

        /// <summary>
        /// Returns one of the active cluster gateways for a given cluster.
        /// </summary>
        /// <param name="cluster">the cluster for which we want a gateway</param>
        /// <returns>a gateway address, or null if none is found for the given cluster</returns>
        SiloAddress GetRandomClusterGateway(string cluster);

        /// <summary>
        /// Subscribe to multicluster configuration change events.
        /// </summary>
        /// <param name="observer">An observer to receive configuration change notifications.</param>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool SubscribeToMultiClusterConfigurationEvents(GrainReference observer);

        /// <summary>
        /// UnSubscribe from multicluster configuration change events.
        /// </summary>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool UnSubscribeFromMultiClusterConfigurationEvents(GrainReference observer);

        /// <summary>
        /// A test hook for dropping protocol messages between replicated grain instances
        /// </summary>
        Func<ILogConsistencyProtocolMessage, bool> ProtocolMessageFilterForTesting { get; set; }
    }
}
