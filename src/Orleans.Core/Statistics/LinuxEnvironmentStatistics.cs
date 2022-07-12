using System;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Statistics
{
    internal class LinuxEnvironmentStatistics : IHostEnvironmentStatistics, ILifecycleObserver, IDisposable
    {
        private readonly ILogger _logger;
        private readonly ObservableCounter<long> _totalPhysicalMemoryCounter;
        private readonly ObservableCounter<long> _availableMemoryCounter;
        private const float KB = 1024f;

        /// <inheritdoc />
        public long? TotalPhysicalMemory { get; private set; }

        /// <inheritdoc />
        public float? CpuUsage { get; private set; }

        /// <inheritdoc />
        public long? AvailableMemory { get; private set; }

        /// <inheritdoc />
        private long MemoryUsage => GC.GetTotalMemory(false);

        private readonly TimeSpan MONITOR_PERIOD = TimeSpan.FromSeconds(5);

        private CancellationTokenSource _cts;
        private Task _monitorTask;

        private const string MEMINFO_FILEPATH = "/proc/meminfo";
        private const string CPUSTAT_FILEPATH = "/proc/stat";

        internal static readonly string[] RequiredFiles = new[]
        {
            MEMINFO_FILEPATH,
            CPUSTAT_FILEPATH
        };

        public LinuxEnvironmentStatistics(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<LinuxEnvironmentStatistics>();
            _totalPhysicalMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.RUNTIME_MEMORY_TOTAL_PHSYSICAL_MEMORY_MB, () => (long)(TotalPhysicalMemory / KB / KB), unit: "MB");
            _availableMemoryCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB, () => (long)(AvailableMemory / KB / KB), unit: "MB");
        }

        public void Dispose()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }

        public async Task OnStart(CancellationToken ct)
        {
            _logger.LogTrace($"Starting {nameof(LinuxEnvironmentStatistics)}");

            _cts = new CancellationTokenSource();
            ct.Register(() => _cts.Cancel());

            _monitorTask = await Task.Factory.StartNew(
                () => Monitor(_cts.Token),
                _cts.Token,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default
            );

            _logger.LogTrace($"Started {nameof(LinuxEnvironmentStatistics)}");
        }

        public async Task OnStop(CancellationToken ct)
        {
            if (_cts == null)
                return;

            _logger.LogTrace($"Stopping {nameof(LinuxEnvironmentStatistics)}");
            try
            {
                _cts.Cancel();
                try
                {
                    await _monitorTask;
                }
                catch (TaskCanceledException) { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping {nameof(LinuxEnvironmentStatistics)}");
            }
            finally
            {
                _logger.LogTrace($"Stopped {nameof(LinuxEnvironmentStatistics)}");
            }
        }

        private async Task UpdateTotalPhysicalMemory()
        {
            var memTotalLine = await ReadLineStartingWithAsync(MEMINFO_FILEPATH, "MemTotal");

            if (string.IsNullOrWhiteSpace(memTotalLine))
            {
                _logger.LogWarning($"Couldn't read 'MemTotal' line from '{MEMINFO_FILEPATH}'");
                return;
            }

            // Format: "MemTotal:       16426476 kB"
            if (!long.TryParse(new string(memTotalLine.Where(char.IsDigit).ToArray()), out var totalMemInKb))
            {
                _logger.LogWarning("Couldn't parse meminfo output");
                return;
            }

            TotalPhysicalMemory = totalMemInKb * 1_000;
        }

        private long _prevIdleTime;
        private long _prevTotalTime;

        private async Task UpdateCpuUsage(int i)
        {
            var cpuUsageLine = await ReadLineStartingWithAsync(CPUSTAT_FILEPATH, "cpu  ");

            if (string.IsNullOrWhiteSpace(cpuUsageLine))
            {
                _logger.LogWarning($"Couldn't read line from '{CPUSTAT_FILEPATH}'");
                return;
            }

            // Format: "cpu  20546715 4367 11631326 215282964 96602 0 584080 0 0 0"
            var cpuNumberStrings = cpuUsageLine.Split(' ').Skip(2);

            if (cpuNumberStrings.Any(n => !long.TryParse(n, out _)))
            {
                _logger.LogWarning($"Failed to parse '{CPUSTAT_FILEPATH}' output correctly. Line: {{CpuUsageLine}}", cpuUsageLine);
                return;
            }

            var cpuNumbers = cpuNumberStrings.Select(long.Parse).ToArray();
            var idleTime = cpuNumbers[3];
            var iowait = cpuNumbers[4]; // Iowait is not real cpu time
            var totalTime = cpuNumbers.Sum() - iowait;

            if (i > 0)
            {
                var deltaIdleTime = idleTime - _prevIdleTime;
                var deltaTotalTime = totalTime - _prevTotalTime;

                // When running in gVisor, /proc/stat returns all zeros, so check here and leave CpuUsage unset.
                // see: https://github.com/google/gvisor/blob/master/pkg/sentry/fs/proc/stat.go#L88-L95
                if (deltaTotalTime == 0f)
                {
                    return;
                }

                var currentCpuUsage = (1.0f - deltaIdleTime / ((float)deltaTotalTime)) * 100f;

                var previousCpuUsage = CpuUsage ?? 0f;
                CpuUsage = (previousCpuUsage + 2 * currentCpuUsage) / 3;
            }

            _prevIdleTime = idleTime;
            _prevTotalTime = totalTime;
        }

        private async Task UpdateAvailableMemory()
        {
            var memAvailableLine = await ReadLineStartingWithAsync(MEMINFO_FILEPATH, "MemAvailable");

            if (string.IsNullOrWhiteSpace(memAvailableLine))
            {
                memAvailableLine = await ReadLineStartingWithAsync(MEMINFO_FILEPATH, "MemFree");
                if (string.IsNullOrWhiteSpace(memAvailableLine))
                {
                    _logger.LogWarning($"Failed to read 'MemAvailable' or 'MemFree' line from '{MEMINFO_FILEPATH}'");
                    return;
                }
            }

            if (!long.TryParse(new string(memAvailableLine.Where(char.IsDigit).ToArray()), out var availableMemInKb))
            {
                _logger.LogWarning("Failed to parse meminfo output: '{MemAvailableLine}'", memAvailableLine);
                return;
            }

            AvailableMemory = availableMemInKb * 1_000;
        }

        private async Task Monitor(CancellationToken ct)
        {
            int i = 0;
            while (true)
            {
                if (ct.IsCancellationRequested)
                    throw new TaskCanceledException("Monitor task canceled");

                try
                {
                    await Task.WhenAll(
                        i == 0 ? UpdateTotalPhysicalMemory() : Task.CompletedTask,
                        UpdateCpuUsage(i),
                        UpdateAvailableMemory()
                    );

                    var logStr = $"LinuxEnvironmentStatistics: CpuUsage={CpuUsage?.ToString("0.0")}, TotalPhysicalMemory={TotalPhysicalMemory}, AvailableMemory={AvailableMemory}";
                    _logger.LogTrace(logStr);

                    await Task.Delay(MONITOR_PERIOD, ct);
                }
                catch (Exception ex) when (ex.GetType() != typeof(TaskCanceledException))
                {
                    _logger.LogError(ex, "LinuxEnvironmentStatistics: error");
                    await Task.Delay(MONITOR_PERIOD + MONITOR_PERIOD + MONITOR_PERIOD, ct);
                }
                if (i < 2)
                    i++;
            }
        }

        private static async Task<string> ReadLineStartingWithAsync(string path, string lineStartsWith)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 512, FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var r = new StreamReader(fs, Encoding.ASCII))
            {
                string line;
                while ((line = await r.ReadLineAsync()) != null)
                {
                    if (line.StartsWith(lineStartsWith, StringComparison.Ordinal))
                        return line;
                }
            }

            return null;
        }
    }
}
