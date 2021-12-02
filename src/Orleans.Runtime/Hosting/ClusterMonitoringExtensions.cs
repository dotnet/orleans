using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Hosting.Clustering;

namespace Orleans.Runtime.Hosting
{
    public static class ClusterMonitoringExtensions
    {
        /// <summary>
        /// Adds cluster monitoring.
        /// </summary>
        public static IServiceCollection UseClusterMonitoring(this IServiceCollection services)
        {
            // Configure defaults based on the current environment.
            services.AddOptions<ClusterMonitoringOptions>();
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, ClusterAgent>();

            return services;
        }
    }
}
