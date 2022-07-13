using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
}
