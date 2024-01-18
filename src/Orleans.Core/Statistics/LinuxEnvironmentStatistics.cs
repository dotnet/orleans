using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Statistics;

internal sealed class LinuxEnvironmentStatistics(ILoggerFactory loggerFactory)
    : EnvironmentStatisticsBase<LinuxEnvironmentStatistics>(loggerFactory)
{
    internal static readonly string MEMINFO_FILEPATH = "/proc/meminfo";

    protected override async ValueTask<long?> GetAvailableMemory(CancellationToken cancellationToken)
    {
        var memAvailableLine = await ReadLineStartingWithAsync(MEMINFO_FILEPATH, "MemAvailable");

        if (string.IsNullOrWhiteSpace(memAvailableLine))
        {
            memAvailableLine = await ReadLineStartingWithAsync(MEMINFO_FILEPATH, "MemFree");
            if (string.IsNullOrWhiteSpace(memAvailableLine))
            {
                _logger.LogWarning("Failed to read 'MemAvailable' or 'MemFree' line from {MEMINFO_FILEPATH}", MEMINFO_FILEPATH);
                return null;
            }
        }

        if (!long.TryParse(new string(memAvailableLine.Where(char.IsDigit).ToArray()), out var availableMemInKb))
        {
            _logger.LogWarning("Failed to parse meminfo output: '{MemAvailableLine}'", memAvailableLine);
            return null;
        }

        return (long)(availableMemInKb * OneKiloByte);

        static async Task<string> ReadLineStartingWithAsync(string path, string lineStartsWith)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 512, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var r = new StreamReader(fs, Encoding.ASCII);

            string line;
            while ((line = await r.ReadLineAsync()) != null)
            {
                if (line.StartsWith(lineStartsWith, StringComparison.Ordinal))
                    return line;
            }

            return null;
        }
    }
}

internal class LinuxEnvironmentStatisticsLifecycleAdapter<TLifecycle>
        : ILifecycleParticipant<TLifecycle>, ILifecycleObserver where TLifecycle : ILifecycleObservable
{
    private readonly LinuxEnvironmentStatistics _statistics;

    public LinuxEnvironmentStatisticsLifecycleAdapter(LinuxEnvironmentStatistics statistics)
    {
        _statistics = statistics;
    }

    public Task OnStart(CancellationToken ct) => _statistics.OnStart(ct);

    public Task OnStop(CancellationToken ct) => _statistics.OnStop(ct);

    public void Participate(TLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(LinuxEnvironmentStatistics),
            ServiceLifecycleStage.RuntimeInitialize,
            this);
    }
}