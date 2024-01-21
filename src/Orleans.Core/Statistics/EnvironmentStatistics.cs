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

    /// <inheritdoc />
    public float? CpuUsagePercentage => _eventCounterListener.CpuUsage.HasValue ? (float)_eventCounterListener.CpuUsage : null;

    /// <inheritdoc />
    public long? MemoryUsageBytes => GC.GetTotalMemory(false) + GC.GetGCMemoryInfo().FragmentedBytes;
    
    /// <inheritdoc />
    public long? AvailableMemoryBytes
    {
        get
        {
            var info = GC.GetGCMemoryInfo();

            var committedOfLimit = info.TotalAvailableMemoryBytes - info.TotalCommittedBytes;
            var unusedLoad = info.HighMemoryLoadThresholdBytes - info.MemoryLoadBytes;
            var systemAvailable = Math.Max(0, Math.Min(committedOfLimit, unusedLoad));
            var processAvailable = info.TotalCommittedBytes - info.HeapSizeBytes;

            return systemAvailable + processAvailable;
        }
    }

    /// <inheritdoc />
    public long? MaximumAvailableMemoryBytes
    {
        get
        {
            var info = GC.GetGCMemoryInfo();
            var physicalMemory = Math.Min(info.TotalAvailableMemoryBytes, info.HighMemoryLoadThresholdBytes);

            return physicalMemory;
        }
    }

    public EnvironmentStatistics()
    {
        GC.Collect(0, GCCollectionMode.Forced, true); // we make sure the GC structure wont be empty, also performing a blocking GC guarantees immediate collection.

        _availableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB, () => (long)(AvailableMemoryBytes ?? 0 / OneKiloByte / OneKiloByte), unit: "MB");
        _maximumAvailableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB, () => (long)(MaximumAvailableMemoryBytes ?? 0 / OneKiloByte / OneKiloByte), unit: "MB");
    }

    public void Dispose() => _eventCounterListener.Dispose();

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