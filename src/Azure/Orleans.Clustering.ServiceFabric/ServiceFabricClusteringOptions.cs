using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for Service Fabric cluster membership.
    /// </summary>
    public class ServiceFabricClusteringOptions
    {
        /// <summary>
        /// Gets or sets the period of time before unknown silos are considered dead.
        /// </summary>
        public TimeSpan UnknownSiloRemovalPeriod { get; set; } = TimeSpan.FromMinutes(1);
    }
}