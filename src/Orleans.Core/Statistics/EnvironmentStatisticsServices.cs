using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.Runtime;

namespace Orleans.Statistics;

internal static class EnvironmentStatisticsServices
{
    internal static IServiceCollection RegisterEnvironmentStatisticsServices<TLifecycleObservable>(this IServiceCollection services)
        where TLifecycleObservable : ILifecycleObservable
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!File.Exists(LinuxEnvironmentStatistics.MEMINFO_FILEPATH))
            {
                throw new OrleansConfigurationException($"{LinuxEnvironmentStatistics.MEMINFO_FILEPATH} file is missing");
            }

            services.AddSingleton<LinuxEnvironmentStatistics>();
            services.AddFromExisting<IHostEnvironmentStatistics, LinuxEnvironmentStatistics>();
            services.AddSingleton<ILifecycleParticipant<TLifecycleObservable>, LinuxEnvironmentStatisticsLifecycleAdapter<TLifecycleObservable>>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<WindowsEnvironmentStatistics>();
            services.AddFromExisting<IHostEnvironmentStatistics, WindowsEnvironmentStatistics>();
            services.AddSingleton<ILifecycleParticipant<TLifecycleObservable>, WindowsEnvironmentStatisticsLifecycleAdapter<TLifecycleObservable>>();
        }
        else
        {
            services.TryAddSingleton<IHostEnvironmentStatistics, NoOpHostEnvironmentStatistics>();
        }

        return services;
    }
}
