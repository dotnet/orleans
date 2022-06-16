using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal class GrainCountStatisticsListener
{
    internal static readonly ConcurrentDictionary<string, int> GrainCounts = new();
    private static readonly MeterListener MeterListener = new();
    static GrainCountStatisticsListener()
    {
        MeterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument == MiscInstruments.GrainCounts)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        MeterListener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
    }

    // TODO: not sure if it's thread-safe... need check
    private static void OnMeasurementRecorded(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
    {
        var typeTag = tags[0];
        var grainType = (string)typeTag.Value;
        GrainCounts.AddOrUpdate(grainType, 1, (k, v) => Interlocked.Increment(ref v));
    }
}
