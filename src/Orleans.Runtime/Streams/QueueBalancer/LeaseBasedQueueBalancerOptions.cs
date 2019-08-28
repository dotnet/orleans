using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Config for LeaseBasedQueueBalancer. User need to configure this option in order to use LeaseBasedQueueBalancer in the
    ///   stream provider.  Per stream provider options can be configured as named options using the same name as the provider.
    /// </summary>
    public class LeaseBasedQueueBalancerOptions
    {
        /// <summary>
        /// LeaseLength
        /// </summary>
        public TimeSpan LeaseLength { get; set; } = DefaultLeaseLength;
        public static readonly TimeSpan DefaultLeaseLength = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Lease renew period
        /// </summary>
        public TimeSpan LeaseRenewPeriod { get; set; } = DefaultLeaseRenewPeriod;
        // DefaultLeaseRenewPeriod set to (DefaultLeaseLength/2 - 1) to allow time for at least 2 renew calls before we lose the lease.
        public static readonly TimeSpan DefaultLeaseRenewPeriod = TimeSpan.FromSeconds(29); 

        /// <summary>
        /// How often balancer attempts to aquire leases to minimum number of queues.
        /// </summary>
        public TimeSpan MinLeaseAquisitionPeriod { get; set; } = DefaultMinLeaseAquisitionPeriod;
        public static readonly TimeSpan DefaultMinLeaseAquisitionPeriod = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How often balancer attempts to aquire leases to maximum number of queues.
        /// </summary>
        public TimeSpan MaxLeaseAquisitionPeriod { get; set; } = DefaultMaxLeaseAquisitionPeriod;
        public static readonly TimeSpan DefaultMaxLeaseAquisitionPeriod = TimeSpan.FromSeconds(90);

        /// <summary>
        /// If greedy, balancer will always try to aquire max leases.
        /// This can improve recovery time at the cost of potentially less evenly balanced queue distribution.
        /// </summary>
        public bool Greedy { get; set; } = DefaultGreedy;
        public const bool DefaultGreedy = false;
    }
}
