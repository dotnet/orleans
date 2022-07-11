using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration.Internal;

namespace Orleans.Statistics
{
    internal static class LinuxEnvironmentStatisticsServices
    {
        /// <summary>
        /// Registers <see cref="LinuxEnvironmentStatistics"/> services.
        /// </summary>
        internal static void RegisterServices<TLifecycleObservable>(IServiceCollection services) where TLifecycleObservable : ILifecycleObservable
        {
            var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            if (!isLinux)
            {
                var logger = services.BuildServiceProvider().GetService<ILogger<LinuxEnvironmentStatistics>>();
                logger?.LogWarning((int)ErrorCode.OS_InvalidOS, LinuxEnvironmentStatisticsValidator.InvalidOS);

                return;
            }

            services.AddTransient<IConfigurationValidator, LinuxEnvironmentStatisticsValidator>();
            services.AddSingleton<LinuxEnvironmentStatistics>();
            services.AddFromExisting<IHostEnvironmentStatistics, LinuxEnvironmentStatistics>();
            services.AddSingleton<ILifecycleParticipant<TLifecycleObservable>, LinuxEnvironmentStatisticsLifecycleAdapter<TLifecycleObservable>>();
        }
    }
}
