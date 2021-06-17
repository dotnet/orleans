using Orleans.Configuration;
using System.Collections.Generic;
using System.Diagnostics;

namespace OneBoxDeployment.OrleansUtilities
{
    /// <summary>
    /// Orleans cluster configuration parameters.
    /// </summary>
    [DebuggerDisplay("ClusterConfig(ServiceId = {ClusterOptions.ServiceId}, ClusterId = {ClusterOptions.ClusterId})")]
    public sealed class ClusterConfig
    {
        /// <summary>
        /// The cluster configuration information.
        /// </summary>
        public ClusterOptions ClusterOptions { get; set; } = new ClusterOptions();

        /// <summary>
        /// The cluster endpoint information.
        /// </summary>
        public EndpointOptions EndPointOptions { get; set; } = new EndpointOptions();

        /// <summary>
        /// Configuration to membership storage.
        /// </summary>
        public ConnectionConfig ConnectionConfig { get; set; } = new ConnectionConfig();

        /// <summary>
        /// The persistent storage configurations.
        /// </summary>
        public IList<ConnectionConfig> StorageConfigs { get; set; } = new List<ConnectionConfig>();

        /// <summary>
        /// The persistent reminder configurations.
        /// </summary>
        public IList<ConnectionConfig> ReminderConfigs { get; set; } = new List<ConnectionConfig>();
    }
}
