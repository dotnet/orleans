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
        /// LeaseProviderType
        /// </summary>
        public Type LeaseProviderType { get; set; }
        /// <summary>
        /// LeaseProviderTypeName
        /// </summary>
        public const string LeaseProviderTypeName = nameof(LeaseProviderType);
        /// <summary>
        /// LeaseLength
        /// </summary>
        public TimeSpan LeaseLength { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>
        /// LeaseLengthName
        /// </summary>
        public const string LeaseLengthName = nameof(LeaseLengthName);
    }
}
