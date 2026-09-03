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
        /// When enabled, dissemination replaces per-refresh direct fan-out only for active peers which have recently
        /// confirmed support for this namespace. Confirmations expire after twice the greater of the anti-entropy
        /// interval and this namespace's expected update cadence. During rolling or mixed-version operation, active
        /// peers without a current confirmation continue to receive direct publications. If dissemination is
        /// unavailable, declines the update, throws, or cannot accept it within the refresh interval, direct
        /// publication targets all active peers.
        /// </remarks>
        public DisseminationNamespaceOptions Dissemination { get; set; } = new() { ExpectedUpdateCadence = TimeSpan.FromSeconds(5) };
    }
}
