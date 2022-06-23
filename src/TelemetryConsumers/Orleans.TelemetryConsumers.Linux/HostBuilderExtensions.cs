using Orleans.Runtime;
using Orleans.Statistics;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Use Linux host environment statistics
        /// </summary>
        public static ISiloBuilder UseLinuxEnvironmentStatistics(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(LinuxEnvironmentStatisticsServices.RegisterServices<ISiloLifecycle>);
        }
    }

    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Use Linux host environment statistics
        /// </summary>
        public static IClientBuilder UseLinuxEnvironmentStatistics(this IClientBuilder builder)
        {
            return builder.ConfigureServices(LinuxEnvironmentStatisticsServices.RegisterServices<IClusterClientLifecycle>);
        }
    }
}