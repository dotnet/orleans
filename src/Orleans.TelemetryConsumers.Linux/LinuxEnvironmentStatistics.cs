using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Statistics
{
    internal class LinuxEnvironmentStatistics : IHostEnvironmentStatistics, ILifecycleParticipant<ISiloLifecycle>, ILifecycleParticipant<IClusterClientLifecycle>, ILifecycleObserver, IDisposable
    {
        private readonly ILogger _logger;

        public long? TotalPhysicalMemory { get; private set; }

        public float? CpuUsage { get; private set; }

        public long? AvailableMemory { get; private set; }

        private readonly TimeSpan MONITOR_PERIOD = TimeSpan.FromSeconds(5);

        private CancellationTokenSource _cts;
        private Task _monitorTask;

        public LinuxEnvironmentStatistics(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<LinuxEnvironmentStatistics>();
        }

        public void Dispose()
        {
            _cts?.Dispose();
            _monitorTask?.Dispose();
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(ServiceLifecycleStage.RuntimeInitialize, this);
        }

        public void Participate(IClusterClientLifecycle lifecycle)
        {
            lifecycle.Subscribe(ServiceLifecycleStage.RuntimeInitialize, this);
        }

        public async Task OnStart(CancellationToken ct)
        {
            _cts = new CancellationTokenSource();
            ct.Register(() => _cts.Cancel());

            _monitorTask = await Task.Factory.StartNew(
                () => Monitor(_cts.Token),
                _cts.Token,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default
            );
        }

        public async Task OnStop(CancellationToken ct)
        {
            _cts.Cancel();
            try
            {
                await _monitorTask;
            }
            catch (TaskCanceledException) { }
        }

        private async Task UpdateTotalPhysicalMemory(CancellationToken ct)
        {
            var memTotalLine = await ReadLineStartingWithAsync("/proc/meminfo", "MemTotal", ct);

            if (string.IsNullOrWhiteSpace(memTotalLine))
            {
                _logger.LogWarning("Couldn't read 'MemTotal' line from '/proc/meminfo'");
                return;
            }

            // Format: "MemTotal:       16426476 kB"
            if (!long.TryParse(new string(memTotalLine.Where(char.IsDigit).ToArray()), out var totalMemInKb))
            {
                _logger.LogWarning($"Couldn't parse meminfo output");
                return;
            }

            TotalPhysicalMemory = totalMemInKb * 1_000;
        }

        private long _prevIdleTime;
        private long _prevTotalTime;

        private async Task UpdateCpuUsage(int i, CancellationToken ct)
        {
            var cpuUsageLine = await ReadLineStartingWithAsync("/proc/stat", "cpu  ", ct);

            if (string.IsNullOrWhiteSpace(cpuUsageLine))
            {
                _logger.LogWarning("Couldn't read line from '/proc/stat'");
                return;
            }

            // Format: "cpu  20546715 4367 11631326 215282964 96602 0 584080 0 0 0"
            var nums = cpuUsageLine.Split(' ').Skip(2).Select(long.Parse).ToArray();
            var idleTime = nums[3];
            var totalTime = nums.Sum();

            if (i > 0)
            {
                var deltaIdleTime = idleTime - _prevIdleTime;
                var deltaTotalTime = totalTime - _prevTotalTime;

                var cpuUsage = (1.0f - deltaIdleTime / ((float)deltaTotalTime)) * 100f;

                CpuUsage = ((CpuUsage ?? 0f) + (2f * cpuUsage)) / 3f;
            }

            _prevIdleTime = idleTime;
            _prevTotalTime = totalTime;
        }

        private async Task UpdateAvailableMemory(int _, CancellationToken ct)
        {
            var memAvailableLine = await ReadLineStartingWithAsync("/proc/meminfo", "MemAvailable", ct);

            if (string.IsNullOrWhiteSpace(memAvailableLine))
            {
                _logger.LogWarning("Couldn't read 'MemAvailable' line from '/proc/meminfo'");
                return;
            }

            if (!long.TryParse(new string(memAvailableLine.Where(char.IsDigit).ToArray()), out var availableMemInKb))
            {
                _logger.LogWarning($"Couldn't parse meminfo output: '{memAvailableLine}'");
                return;
            }

            AvailableMemory = availableMemInKb * 1_000;
        }

        private async Task Monitor(CancellationToken ct)
        {
            for (int i = 0; ; i++)
            {
                if (ct.IsCancellationRequested)
                    throw new TaskCanceledException("Monitor task canceled");

                try
                {
                    if (i == 0)
                    {
                        await UpdateTotalPhysicalMemory(ct);
                    }

                    await Task.WhenAll(
                        UpdateCpuUsage(i, ct),
                        UpdateAvailableMemory(i, ct)
                    );

                    _logger.LogTrace($"LinuxEnvironmentStatistics: CPU={CpuUsage?.ToString("0.0")}, MemTotal={TotalPhysicalMemory}, MemAvailable={AvailableMemory}");

                    await Task.Delay(MONITOR_PERIOD, ct);
                }
                catch (Exception ex) when (ex.GetType() != typeof(TaskCanceledException))
                {
                    _logger.LogError(ex, "LinuxEnvironmentStatistics: error");
                    await Task.Delay(MONITOR_PERIOD + MONITOR_PERIOD + MONITOR_PERIOD, ct);
                }
            }
        }

        private static async Task<string> ReadLineStartingWithAsync(string path, string lineStartsWith, CancellationToken ct)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var r = new StreamReader(fs, Encoding.ASCII))
            {
                string line;
                while ((line = await r.ReadLineAsync()) != null && line.StartsWith(lineStartsWith))
                {
                    return line;
                }
            }

            return null;
        }
    }
}
