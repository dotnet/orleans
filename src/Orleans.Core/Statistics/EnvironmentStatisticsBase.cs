using System;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;
using Orleans.Runtime;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace Orleans.Statistics;

#nullable enable

/// <summary>
/// Base class for environment statistics
/// </summary>
internal abstract class EnvironmentStatisticsBase<T> : IHostEnvironmentStatistics, ILifecycleObserver, IDisposable
    where T : EnvironmentStatisticsBase<T>
{
    private Task? _monitorTask;
    private const byte _monitorPeriodSecs = 5;

    private readonly EventCounterListener _eventCounterListener = new(_monitorPeriodSecs.ToString());
    private readonly CancellationTokenSource _cts = new();
    private readonly ObservableCounter<long> _availableMemoryCounter;
    private readonly ObservableCounter<long> _totalPhysicalMemoryCounter;

    protected const float OneKiloByte = 1024;

    protected ILogger _logger;
    protected TimeSpan _monitorPeriod = TimeSpan.FromSeconds(_monitorPeriodSecs);

    /// <inheritdoc />
    public float? CpuUsage => _eventCounterListener.CpuUsage.HasValue ? (float)_eventCounterListener.CpuUsage : null;
    /// <inheritdoc />
    public long? TotalPhysicalMemory => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    /// <inheritdoc />
    public long? AvailableMemory { get; protected set; }

    protected EnvironmentStatisticsBase(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<T>();

        if (GC.CollectionCount(0) == 0)
        {
            // We make sure the GC structure wont be empty, also performing a blocking GC guarantees immediate completion.
            GC.Collect(0, GCCollectionMode.Forced, true);
        }

        _availableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB, () => (long)(AvailableMemory ?? 0 / OneKiloByte / OneKiloByte), unit: "MB");
        _totalPhysicalMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB, () => (long)(TotalPhysicalMemory ?? 0 / OneKiloByte / OneKiloByte), unit: "MB");
    }

    protected abstract ValueTask<long?> GetAvailableMemory(CancellationToken cancellationToken);

    private async Task Monitor(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException("Monitor task canceled");
            }

            try
            {
                AvailableMemory = await GetAvailableMemory(cancellationToken);

                LogStatistics();

                await Task.Delay(_monitorPeriod, cancellationToken);
            }
            catch (Exception ex) when (ex.GetType() != typeof(TaskCanceledException))
            {
                _logger.LogError(ex, "{Statistics}: error", nameof(LinuxEnvironmentStatistics));
                await Task.Delay(3 * _monitorPeriod, cancellationToken);
            }
        }
    }

    public async Task OnStart(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Starting {Statistics}", typeof(T).Name);

        using var _ = cancellationToken.Register(_cts.Cancel);

        _monitorTask = await Task.Factory.StartNew(
            () => Monitor(_cts.Token),
            _cts.Token,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default
        );

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Started {Statistics}", typeof(T).Name);
    }

    public async Task OnStop(CancellationToken cancellationToken)
    {
        if (_cts == null)
            return;

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Stopping {Statistics}", typeof(T).Name);

        try
        {
            _cts.Cancel();

            if (_monitorTask is null)
            {
                return;
            }

            await _monitorTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping {Statistics}", typeof(T).Name);
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Stopped {Statistics}", typeof(T).Name);
        }
    }

    protected void LogStatistics()
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("{Statistics}: CpuUsage={CpuUsageValue}, TotalPhysicalMemory={TotalPhysicalMemoryValue}, AvailableMemory={AvailableMemoryValue}",
                typeof(T).Name, CpuUsage?.ToString("0.0"), TotalPhysicalMemory, AvailableMemory);
    }

    public void Dispose()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    private sealed class EventCounterListener(string pollingInterval) : EventListener
    {
        private readonly string _pollingInterval = pollingInterval;

        public double? CpuUsage { get; private set; }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name.Equals("System.Runtime"))
            {
                Dictionary<string, string?>? refreshInterval = new() { ["EventCounterIntervalSec"] = _pollingInterval };
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