using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring deployment load publishing.
    /// </summary>
    public class DeploymentLoadPublisherOptions
    {
        /// <summary>
        /// Interval in which deployment statistics are published.
        /// </summary>
        public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; } = DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME;

        /// <summary>
        /// The default value for <see cref="DeploymentLoadPublisherRefreshTime"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets dissemination options for deployment load statistics.
        /// </summary>
        /// <remarks>
        /// When enabled, dissemination replaces per-refresh direct fan-out. Direct publication is used when the
        /// dissemination subsystem cannot accept the update within the refresh interval.
        /// Keep dissemination disabled during rolling upgrades which include silos that do not support this
        /// namespace. Enable it after every silo has been upgraded and configured consistently.
        /// </remarks>
        public DisseminationNamespaceOptions Dissemination { get; set; } = new() { ExpectedUpdateCadence = TimeSpan.FromSeconds(5) };
    }
}
