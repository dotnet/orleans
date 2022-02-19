using System;
using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for <see cref="DeploymentBasedQueueBalancer"/>.
    /// </summary>
    public class DeploymentBasedQueueBalancerOptions
    {
        /// <summary>
        /// Gets or sets the silo maturity period, which is the period of time to allow a silo to remain active for before rebalancing queues.
        /// </summary>
        /// <value>The silo maturity period.</value>
        public TimeSpan SiloMaturityPeriod { get; set; } = DEFAULT_SILO_MATURITY_PERIOD;

        /// <summary>
        /// The default silo maturity period.
        /// </summary>
        public static readonly TimeSpan DEFAULT_SILO_MATURITY_PERIOD = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets or sets a value indicating whether to presume a static (fixed) cluster.
        /// </summary>
        public bool IsFixed { get; set; }
    }
}
