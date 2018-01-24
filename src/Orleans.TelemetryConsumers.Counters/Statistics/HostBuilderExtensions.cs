#define LOG_MEMORY_PERF_COUNTERS

using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Statistics
{
    public static class SiloHostBuilderExtensions
    {
        /// <summary>
        /// Use Windows performance counters as source for host environment statistics
        /// </summary>
        public static ISiloHostBuilder UsePerfCounterEnvironmentStatistics(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.AddSingleton<IHostEnvironmentStatistics, PerfCounterEnvironmentStatistics>());
        }
    }

    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Use Windows performance counters as source for host environment statistics
        /// </summary>
        public static IClientBuilder UsePerfCounterEnvironmentStatistics(this IClientBuilder builder)
        {
            return builder.ConfigureServices(services => services.AddSingleton<IHostEnvironmentStatistics, PerfCounterEnvironmentStatistics>());
        }
    }
}