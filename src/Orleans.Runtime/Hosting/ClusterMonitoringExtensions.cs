using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Hosting.Clustering;

namespace Orleans.Runtime.Hosting;

/// <summary>
/// Provides extensions for configuring external cluster monitoring.
/// </summary>
public static class ClusterMonitoringExtensions
{
    /// <summary>
    /// Adds external cluster monitoring.
    /// </summary>
    public static IServiceCollection UseClusterMonitoring(this IServiceCollection services)
    {
        services.AddOptions<ClusterMonitoringOptions>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, ClusterAgent>();

        return services;
    }
}
