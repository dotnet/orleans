using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring environment statistics.
    /// </summary>
    public static class EnvironmentStatisticsHostingExtensions
    {
        /// <summary>
        /// Configures the CPU usage collection interval.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configurator">The configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder ConfigureCPUUsageCollectionInterval(this ISiloBuilder builder, Action<EnvironmentStatisticsOptions> configurator)
        {
            return builder.ConfigureServices(services => services.Configure(configurator));
        }

        /// <summary>
        /// Configures the CPU usage collection interval.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <param name="configurator">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder ConfigureCPUUsageCollectionInterval(this IClientBuilder builder, Action<EnvironmentStatisticsOptions> configurator)
        {
            return builder.ConfigureServices(services => services.Configure(configurator));
        }
    }
}