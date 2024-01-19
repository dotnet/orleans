using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Statistics;

#nullable enable
internal sealed class HostEnvironmentStatistics : IHostEnvironmentStatistics, ILifecycleObserver, IDisposable
{
    private const float OneKiloByte = 1024f;

    private Task? _loggingTask;
    private CancellationTokenSource? _cts;

    private readonly ILogger _logger;
    private readonly EventCounterListener _eventCounterListener = new();
    private readonly ObservableCounter<long> _availableMemoryCounter;
    private readonly ObservableCounter<long> _totalPhysicalMemoryCounter;

    private static readonly TimeSpan LoggingPeriod = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public float? CpuUsage => _eventCounterListener.CpuUsage.HasValue ? (float)_eventCounterListener.CpuUsage : null;
    /// <inheritdoc />
    public long? TotalPhysicalMemory => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    /// <inheritdoc />
    public long? AvailableMemory
    {
        get
        {
            var MemoryInfo = GC.GetGCMemoryInfo();

            double fragmentedMemory =
                 0.45d * MemoryInfo.GenerationInfo[0].FragmentationAfterBytes + // Gen0: small and short-lived objects, making fragmented space more usable
                 0.60d * MemoryInfo.GenerationInfo[1].FragmentationAfterBytes + // Gen1: objects here can vary in size and lifetime, making fragmented space less usable
                 0.85d * MemoryInfo.GenerationInfo[2].FragmentationAfterBytes + // Gen2: long-lived objects and less frequent collection, making fragmented space a lot less usable
                 0.95d * MemoryInfo.GenerationInfo[3].FragmentationAfterBytes + // LOH:  very challenging ton reclaim fragmented space
                         MemoryInfo.GenerationInfo[4].FragmentationAfterBytes;  // POH:  pinned objects cannot be moved, effectively rendering fragmented space unusable

            var availableMemory = Math.Max(0, MemoryInfo.HighMemoryLoadThresholdBytes - (MemoryInfo.MemoryLoadBytes - (long)fragmentedMemory));
            return availableMemory;
        }
    }

    public HostEnvironmentStatistics(ILoggerFactory loggerFactory)
    {
        GC.Collect(0, GCCollectionMode.Forced, true); // we make sure the GC structure wont be empty, also performing a blocking GC guarantees immediate collection.

        _logger = loggerFactory.CreateLogger<HostEnvironmentStatistics>();
        _availableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB, () => (long)(AvailableMemory ?? 0 / OneKiloByte / OneKiloByte), unit: "MB");
        _totalPhysicalMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB, () => (long)(TotalPhysicalMemory ?? 0 / OneKiloByte / OneKiloByte), unit: "MB");
    }

    private async Task LogStatsPeriodically(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException("Periodic stats logging task has been canceled");
            }

            try
            {
                _logger.LogTrace("{Statistics}: CpuUsage={CpuUsageValue}, TotalPhysicalMemory={TotalPhysicalMemoryValue}, AvailableMemory={AvailableMemoryValue}",
                    nameof(HostEnvironmentStatistics), CpuUsage?.ToString("0.0"), TotalPhysicalMemory, AvailableMemory);

                await Task.Delay(LoggingPeriod, cancellationToken);
            }
            catch (Exception ex) when (ex.GetType() != typeof(TaskCanceledException))
            {
                _logger.LogError(ex, "{Statistics}: error", nameof(HostEnvironmentStatistics));
                await Task.Delay(3 * LoggingPeriod, cancellationToken);
            }
        }
    }

    public async Task OnStart(CancellationToken cancellationToken)
    {
        if (!_logger.IsEnabled(LogLevel.Trace))
            return;  // If tracing is not enabled, we don't need any periodic logging of the stats, so we avoid creation of the _loggingTask & _cts

        _logger.LogTrace("Starting {Statistics} collection", nameof(HostEnvironmentStatistics));

        _cts = new();
        using var _ = cancellationToken.Register(_cts.Cancel);

        _loggingTask = await Task.Factory.StartNew(
            () => LogStatsPeriodically(_cts.Token),
            _cts.Token,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default
        );

        _logger.LogTrace("Started {Statistics} collection", nameof(HostEnvironmentStatistics));
    }

    public async Task OnStop(CancellationToken cancellationToken)
    {
        if (_cts is null || !_logger.IsEnabled(LogLevel.Trace))
            return;  

        _logger.LogTrace("Stopping {Statistics} collection", nameof(HostEnvironmentStatistics));

        try
        {
            _cts.Cancel();

            if (_loggingTask is null)
            {
                return;
            }

            await _loggingTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping {Statistics} collection", nameof(HostEnvironmentStatistics));
        }
        finally
        {
            _logger.LogTrace("Stopped {Statistics} collection", nameof(HostEnvironmentStatistics));
        }
    }

    public void Dispose()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    private sealed class EventCounterListener : EventListener
    {
        public double? CpuUsage { get; private set; }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name.Equals("System.Runtime"))
            {
                Dictionary<string, string?>? refreshInterval = new() { ["EventCounterIntervalSec"] = "0.5" };
                EnableEvents(source, EventLevel.Informational, (EventKeywords)(-1), refreshInterval);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName!.Equals("EventCounters"))
            {
                for (int i = 0; i < eventData.Payload!.Count; i++)
                {
                    if (eventData.Payload![i] is IDictionary<string, object> eventPayload)
                    {
                        if (eventPayload.TryGetValue("Name", out var name) && "cpu-usage".Equals(name))
                        {
                            if (eventPayload.TryGetValue("Mean", out var mean))
                            {
                                CpuUsage = (double)mean;
                            }
                        }
                    }
                }
            }
        }
    }
}

internal sealed class HostEnvironmentStatisticsLifecycleAdapter<TLifecycle> : ILifecycleParticipant<TLifecycle>, ILifecycleObserver
    where TLifecycle : ILifecycleObservable
{
    private readonly HostEnvironmentStatistics _statistics;

    public HostEnvironmentStatisticsLifecycleAdapter(HostEnvironmentStatistics statistics)
        => _statistics = statistics;

    public Task OnStart(CancellationToken ct) => _statistics.OnStart(ct);
    public Task OnStop(CancellationToken ct) => _statistics.OnStop(ct);

    public void Participate(TLifecycle lifecycle) =>
        lifecycle.Subscribe(
            nameof(HostEnvironmentStatistics),
            ServiceLifecycleStage.RuntimeInitialize,
            this);
}