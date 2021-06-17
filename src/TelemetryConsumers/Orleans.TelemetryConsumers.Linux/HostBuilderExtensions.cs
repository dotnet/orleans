#define LOG_MEMORY_PERF_COUNTERS

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Statistics
{
    /// <summary>
    /// Validates <see cref="LinuxEnvironmentStatistics"/> requirements for.
    /// </summary>
    internal class LinuxEnvironmentStatisticsValidator : IConfigurationValidator
    {
        internal static readonly string InvalidOS = $"Tried to add '{nameof(LinuxEnvironmentStatistics)}' on non-linux OS";

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            if (!isLinux)
            {
                throw new OrleansConfigurationException(InvalidOS);
            }

            var missingFiles = LinuxEnvironmentStatistics.RequiredFiles
                .Select(f => new { FilePath = f, FileExists = File.Exists(f) })
                .Where(f => !f.FileExists)
                .ToList();

            if (missingFiles.Any())
            {
                var paths = string.Join(", ", missingFiles.Select(f => f.FilePath));
                throw new OrleansConfigurationException($"Missing files for {nameof(LinuxEnvironmentStatistics)}: {paths}");
            }
        }
    }

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
            services.AddFromExisting(typeof(ILifecycleParticipant<TLifecycleObservable>), typeof(LinuxEnvironmentStatistics));
        }
    }

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