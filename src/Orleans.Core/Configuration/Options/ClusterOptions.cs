using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the Orleans cluster.
    /// </summary>
    public class ClusterOptions
    {
        /// <summary>
        /// Default <see cref="ClusterId"/> value.
        /// </summary>
        internal const string DefaultClusterId = "default";

        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; } = DefaultClusterId;

        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment, where as <see cref="ClusterId"/> might not.
        /// </summary>
        public Guid ServiceId { get; set; }
    }
}
