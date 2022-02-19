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
        /// Gets or sets the length of the lease.
        /// </summary>
        /// <value>The length of the lease.</value>
        public TimeSpan LeaseLength { get; set; } = DefaultLeaseLength;

        /// <summary>
        /// The default lease length.
        /// </summary>
        public static readonly TimeSpan DefaultLeaseLength = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the lease renew period.
        /// </summary>
        /// <value>The lease renew period.</value>
        public TimeSpan LeaseRenewPeriod { get; set; } = DefaultLeaseRenewPeriod;

        /// <summary>
        /// The default lease renew period
        /// </summary>
        /// <remarks>
        /// <see cref="DefaultLeaseRenewPeriod"/> set to (<see cref="DefaultLeaseLength"/>/2 - 1) to allow time for at least 2 renew calls before we lose the lease.        
        /// </remarks>
        public static readonly TimeSpan DefaultLeaseRenewPeriod = TimeSpan.FromSeconds(29); 

        /// <summary>
        /// Gets or sets how often balancer attempts to aquire leases.
        /// </summary>
        public TimeSpan LeaseAquisitionPeriod { get; set; } = DefaultMinLeaseAquisitionPeriod;

        /// <summary>
        /// The default minimum lease aquisition period.
        /// </summary>
        public static readonly TimeSpan DefaultMinLeaseAquisitionPeriod = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the lease category, allows for more fine grain partitioning of leases.
        /// </summary>
        public string LeaseCategory { get; set; } = DefaultLeaseCategory;

        /// <summary>
        /// The default lease category
        /// </summary>
        public const string DefaultLeaseCategory = "QueueBalancer";
    }
}
