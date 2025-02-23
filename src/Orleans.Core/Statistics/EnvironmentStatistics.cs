using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using Orleans.Runtime;

namespace Orleans.Statistics;

#nullable enable
internal sealed class EnvironmentStatisticsProvider : IEnvironmentStatisticsProvider, IDisposable
{
    private const float OneKiloByte = 1024f;

    private long _availableMemoryBytes;
    private long _maximumAvailableMemoryBytes;

    private readonly EventCounterListener _eventCounterListener = new();

    [SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Used for memory-dump debugging.")]
    private readonly ObservableCounter<long> _availableMemoryCounter;

    [SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Used for memory-dump debugging.")]
    private readonly ObservableCounter<long> _maximumAvailableMemoryCounter;

    private readonly DualModeKalmanFilter _cpuUsageFilter = new();
    private readonly DualModeKalmanFilter _memoryUsageFilter = new();
    private readonly DualModeKalmanFilter _availableMemoryFilter = new();

    public EnvironmentStatisticsProvider()
    {
        GC.Collect(0, GCCollectionMode.Forced, true); // we make sure the GC structure wont be empty, also performing a blocking GC guarantees immediate collection.

        _availableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB, () => (long)(_availableMemoryBytes / OneKiloByte / OneKiloByte), unit: "MB");
        _maximumAvailableMemoryCounter = Instruments.Meter.CreateObservableCounter(InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB, () => (long)(_maximumAvailableMemoryBytes / OneKiloByte / OneKiloByte), unit: "MB");
    }

    /// <inheritdoc />
public EnvironmentStatistics GetEnvironmentStatistics()
{
    var memoryInfo = GC.GetGCMemoryInfo();

    var cpuUsage = _eventCounterListener.CpuUsage;
    var memoryUsage = GC.GetTotalMemory(false) + memoryInfo.FragmentedBytes;

    var committedOfLimit = memoryInfo.TotalAvailableMemoryBytes - memoryInfo.TotalCommittedBytes;
    var unusedLoad = memoryInfo.HighMemoryLoadThresholdBytes - memoryInfo.MemoryLoadBytes;
    var systemAvailable = Math.Max(0, Math.Min(committedOfLimit, unusedLoad));
    var processAvailable = memoryInfo.TotalCommittedBytes - memoryInfo.HeapSizeBytes;
    var availableMemory = systemAvailable + processAvailable;
    var maxAvailableMemory = Math.Min(memoryInfo.TotalAvailableMemoryBytes, memoryInfo.HighMemoryLoadThresholdBytes);

    var filteredCpuUsage = _cpuUsageFilter.Filter(cpuUsage);
    var filteredMemoryUsage = (long)_memoryUsageFilter.Filter(memoryUsage);
    var filteredAvailableMemory = (long)_availableMemoryFilter.Filter(availableMemory);
    // no need to filter 'maxAvailableMemory' as it will almost always be a steady value.

    _availableMemoryBytes = filteredAvailableMemory;
    _maximumAvailableMemoryBytes = maxAvailableMemory;

    return new(filteredCpuUsage, filteredMemoryUsage, filteredAvailableMemory, maxAvailableMemory);
}

    public void Dispose() => _eventCounterListener.Dispose();

    private sealed class EventCounterListener : EventListener
    {
        public float CpuUsage { get; private set; } = 0f;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name.Equals("System.Runtime"))
            {
                Dictionary<string, string?>? refreshInterval = new() { ["EventCounterIntervalSec"] = "1" };
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
                        CpuUsage = (float)value;
                        break;
                    }
                }
            }
        }
    }

    // See: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/

    // The rationale behind using a cooperative dual-mode KF, is that we want the input signal to follow a trajectory that
    // decays with a slower rate than the original one, but also tracks the signal in case of signal increases
    // (which represent potential of overloading). Both are important, but they are inversely correlated to each other.
    private sealed class DualModeKalmanFilter
    {
        private const float SlowProcessNoiseCovariance = 0f;
        private const float FastProcessNoiseCovariance = 0.01f;

        private KalmanFilter _slowFilter = new();
        private KalmanFilter _fastFilter = new();

        private FilterRegime _regime = FilterRegime.Slow;

        private enum FilterRegime
        {
            Slow,
            Fast
        }

        public float Filter(float measurement)
        {
            float slowEstimate = _slowFilter.Filter(measurement, SlowProcessNoiseCovariance);
            float fastEstimate = _fastFilter.Filter(measurement, FastProcessNoiseCovariance);

            if (measurement > slowEstimate)
            {
                if (_regime == FilterRegime.Slow)
                {
                    _regime = FilterRegime.Fast;
                    _fastFilter.SetState(measurement, 0f);
                    fastEstimate = _fastFilter.Filter(measurement, FastProcessNoiseCovariance);
                }

                return fastEstimate;
            }
            else
            {
                if (_regime == FilterRegime.Fast)
                {
                    _regime = FilterRegime.Slow;
                    _slowFilter.SetState(_fastFilter.PriorEstimate, _fastFilter.PriorErrorCovariance);
                    slowEstimate = _slowFilter.Filter(measurement, SlowProcessNoiseCovariance);
                }

                return slowEstimate;
            }
        }

        private struct KalmanFilter()
        {
            public float PriorEstimate { get; private set; } = 0f;
            public float PriorErrorCovariance { get; private set; } = 1f;

            public void SetState(float estimate, float errorCovariance)
            {
                PriorEstimate = estimate;
                PriorErrorCovariance = errorCovariance;
            }

            public float Filter(float measurement, float processNoiseCovariance)
            {
                float estimate = PriorEstimate;
                float errorCovariance = PriorErrorCovariance + processNoiseCovariance;

                float gain = errorCovariance / (errorCovariance + 1f);
                float newEstimate = estimate + gain * (measurement - estimate);
                float newErrorCovariance = (1f - gain) * errorCovariance;

                PriorEstimate = newEstimate;
                PriorErrorCovariance = newErrorCovariance;

                return newEstimate;
            }
        }
    }
}