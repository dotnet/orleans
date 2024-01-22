using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using Orleans.Runtime;

namespace Orleans.Statistics;

#nullable enable
internal sealed class EnvironmentStatistics : IEnvironmentStatistics, IDisposable
{
    private const float OneKiloByte = 1024f;

    private readonly EventCounterListener _eventCounterListener = new();

    [SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Used for memory-dump debugging.")]
    private readonly ObservableCounter<long> _availableMemoryCounter;

    [SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Used for memory-dump debugging.")]
    private readonly ObservableCounter<long> _maximumAvailableMemoryCounter;

    private long _availableMemoryBytes;
    private long _maximumAvailableMemoryBytes;

    public EnvironmentStatistics()
    {
        GC.Collect(0, GCCollectionMode.Forced, true); // we make sure the GC structure wont be empty, also performing a blocking GC guarantees immediate collection.

        _availableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB, () => (long)(_availableMemoryBytes / OneKiloByte / OneKiloByte), unit: "MB");
        _maximumAvailableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB, () => (long)(_maximumAvailableMemoryBytes / OneKiloByte / OneKiloByte), unit: "MB");
    }

    /// <inheritdoc />
    public HardwareStatistics GetHardwareStatistics()
    {
        var memoryInfo = GC.GetGCMemoryInfo();

        var cpuUsage = (float)_eventCounterListener.CpuUsage;
        var memoryUsage = GC.GetTotalMemory(false) + memoryInfo.FragmentedBytes;

        var committedOfLimit = memoryInfo.TotalAvailableMemoryBytes - memoryInfo.TotalCommittedBytes;
        var unusedLoad = memoryInfo.HighMemoryLoadThresholdBytes - memoryInfo.MemoryLoadBytes;
        var systemAvailable = Math.Max(0, Math.Min(committedOfLimit, unusedLoad));
        var processAvailable = memoryInfo.TotalCommittedBytes - memoryInfo.HeapSizeBytes;
        var availableMemory = systemAvailable + processAvailable;

        var maximumAvailableMemory = Math.Min(memoryInfo.TotalAvailableMemoryBytes, memoryInfo.HighMemoryLoadThresholdBytes);

        _availableMemoryBytes = availableMemory;
        _maximumAvailableMemoryBytes = maximumAvailableMemory;

        return new(cpuUsage, memoryUsage, availableMemory, maximumAvailableMemory);
    }

    public void Dispose() => _eventCounterListener.Dispose();

    private sealed class EventCounterListener : EventListener
    {
        public double CpuUsage { get; private set; } = 0d;

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
            if ("EventCounters".Equals(eventData.EventName) && eventData.Payload is { } payload)
            {
                for (var i = 0; i < payload.Count; i++)
                {
                    if (payload[i] is IDictionary<string, object?> eventPayload
                        && eventPayload.TryGetValue("Name", out var name)
                        && "cpu-usage".Equals(name)
                        && eventPayload.TryGetValue("Mean", out var mean)
                        && mean is double value)
                    {
                        CpuUsage = value;
                        break;
                    }
                }
            }
        }
    }
}