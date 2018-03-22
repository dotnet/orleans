using System;

namespace Orleans.Configuration
{
    public interface IClusterSettings
    {
        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        string ClusterId { get; }

        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment, where as <see cref="ClusterId"/> might not.
        /// </summary>
        Guid ServiceId { get; }
    }

    /// <summary>
    /// Configures the Orleans cluster.
    /// </summary>
    public class ClusterOptions : IClusterSettings
    {
        /// <summary>
        /// Default cluster id for development clusters.
        /// </summary>
        internal const string DevelopmentClusterId = "dev";

        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; }

        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment, where as <see cref="ClusterId"/> might not.
        /// </summary>
        public string ServiceId { get; set; }
    }
}
