using System;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    /// <summary>
    /// Options for configuring multi-cluster support
    /// </summary>
    public class MultiClusterOptions
    {
        /// <summary>
        /// Whether this cluster is configured to be part of a multi-cluster network
        /// </summary>
        public bool HasMultiClusterNetwork { get; set; } = false;

        /// <summary>
        ///A list of cluster ids, to be used if no multi-cluster configuration is found in gossip channels.
        /// </summary>
        public IList<string> DefaultMultiCluster { get; set; } = new List<string>();

        /// <summary>
        /// The maximum number of silos per cluster should be designated to serve as gateways.
        /// </summary>
        public int MaxMultiClusterGateways { get; set; } = 10;

        /// <summary>
        /// The time between background gossips.
        /// </summary>
        public TimeSpan BackgroundGossipInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether to use the global single instance protocol as the default
        /// multi-cluster registration strategy.
        /// </summary>
        public bool UseGlobalSingleInstanceByDefault { get; set; } = true;

        /// <summary>
        /// The number of quick retries before going into DOUBTFUL state.
        /// </summary>
        public int GlobalSingleInstanceNumberRetries { get; set; } = 10;

        /// <summary>
        /// The time between the slow retries for DOUBTFUL activations.
        /// </summary>
        public TimeSpan GlobalSingleInstanceRetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    }
}
