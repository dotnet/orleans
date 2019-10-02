#define LOG_MEMORY_PERF_COUNTERS

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
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

    public static class SiloHostBuilderExtensions
    {
        /// <summary>
        /// Use Linux host environment statistics
        /// </summary>
        public static ISiloHostBuilder UseLinuxEnvironmentStatistics(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services =>
            {
                services.AddTransient<IConfigurationValidator, LinuxEnvironmentStatisticsValidator>();
                services.AddSingleton<LinuxEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, LinuxEnvironmentStatistics>();
                services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LinuxEnvironmentStatistics>();
            });
        }

        /// <summary>
        /// Use Linux host environment statistics
        /// </summary>
        public static ISiloBuilder UseLinuxEnvironmentStatistics(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(services =>
            {
                services.AddTransient<IConfigurationValidator, LinuxEnvironmentStatisticsValidator>();
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
                services.AddTransient<IConfigurationValidator, LinuxEnvironmentStatisticsValidator>();
                services.AddSingleton<LinuxEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, LinuxEnvironmentStatistics>();
                services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, LinuxEnvironmentStatistics>();
            });
        }
    }
}
