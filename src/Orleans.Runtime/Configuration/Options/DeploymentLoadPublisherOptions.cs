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
        /// When all active peers confirm support, producers send updates directly to the aggregation root.
        /// The root seals a cohort after one fresh contribution from each active silo, including itself, or after
        /// <see cref="DeploymentLoadPublisherRefreshTime"/> elapses. Publication receipts complete at sealing and
        /// align subsequent samples with the cohort's next publication boundary. Relays forward admitted cohorts
        /// immediately through the parent/child tree. Batch item and byte limits can split a cohort into multiple wire messages.
        /// Confirmation remains valid for that silo generation
        /// until the peer explicitly rejects the namespace or leaves the eligible membership set. During bootstrap
        /// or mixed-version operation, direct publication covers all active peers so that an unsupported intermediate
        /// node cannot interrupt delivery. Direct publication also covers all active peers when dissemination is
        /// unavailable, declines the update, throws, or exceeds the bounded publication receipt wait.
        /// </remarks>
        public DisseminationNamespaceOptions Dissemination { get; set; } = new()
        {
            ExpectedUpdateCadence = TimeSpan.FromSeconds(5),
            MaxPendingItemCount = 8 * 1024,
        };
    }
}
