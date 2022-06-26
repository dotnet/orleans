using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal static class GrainMetricsListener
{
    internal static readonly ConcurrentDictionary<string, int> GrainCounts = new();
    private static readonly MeterListener MeterListener = new();
    static GrainMetricsListener()
    {
        MeterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == InstrumentNames.GRAIN_COUNTS)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        MeterListener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
    }

    internal static void Start()
    {
        MeterListener.Start();
    }

    // Alternatives:
    // 1. Use existing *Statistics counters
    // 2. Copy source code from System.Diagnostics.Metrics.AggregationManager
    private static void OnMeasurementRecorded(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
    {
        var typeTag = tags[0];
        var grainType = (string)typeTag.Value;
        if (measurement == 1)
        {
            GrainCounts.AddOrUpdate(grainType, 1, (k, v) => Interlocked.Increment(ref v));
        }
        else if (measurement == -1)
        {
            GrainCounts.AddOrUpdate(grainType, -1, (k, v) => Interlocked.Decrement(ref v));
        }
    }
}
