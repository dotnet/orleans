using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Silo configuration options.
    /// </summary>
    public class SiloOptions
    {
        /// <summary>
        /// Gets or sets the silo name.
        /// </summary>
        public string SiloName { get; set; }

        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; }

        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment, where as <see cref="ClusterId"/> might not.
        /// </summary>
        public Guid ServiceId { get; set; }
    }
}