using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Statistics
{
    public static class SiloHostBuilderExtensions
    {
        /// <summary>
        /// Use Linux host environment statistics
        /// </summary>
        public static ISiloHostBuilder UseLinuxEnvironmentStatistics(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services =>
            {
                services.AddSingleton<LinuxEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, LinuxEnvironmentStatistics>();
                services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LinuxEnvironmentStatistics>();
            });
        }
    }

    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Use Linux host environment statistics
        /// </summary>
        public static IClientBuilder UseLinuxEnvironmentStatistics(this IClientBuilder builder)
        {
            return builder.ConfigureServices(services =>
            {
                services.AddSingleton<LinuxEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, LinuxEnvironmentStatistics>();
                services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, LinuxEnvironmentStatistics>();
            });
        }
    }
}
