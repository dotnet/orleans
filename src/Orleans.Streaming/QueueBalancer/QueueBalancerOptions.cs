using System;

namespace Orleans.Configuration
{
    public class DeploymentBasedQueueBalancerOptions
    {
        public TimeSpan SiloMaturityPeriod { get; set; } = DEFAULT_SILO_MATURITY_PERIOD;
        public static readonly TimeSpan DEFAULT_SILO_MATURITY_PERIOD = TimeSpan.FromMinutes(2);
        public bool IsFixed { get; set; }
    }
}
