using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class GrainInstruments
{
    private readonly UpDownCounter<int> _grainCounts;
    private readonly UpDownCounter<int> _systemTargetCounts;

    public GrainInstruments(OrleansInstruments instruments)
    {
        _grainCounts = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.GRAIN_COUNTS);
        _systemTargetCounts = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.SYSTEM_TARGET_COUNTS);
    }

    internal void IncrementGrainCounts(GrainTypeMetrics metrics)
    {
        Interlocked.Increment(ref metrics.GrainCount);
        _grainCounts.Add(1, new KeyValuePair<string, object?>(GrainTypeMetrics.TagName, metrics.GrainTypeTagValue));
    }

    internal void DecrementGrainCounts(GrainTypeMetrics metrics)
    {
        Interlocked.Decrement(ref metrics.GrainCount);
        _grainCounts.Add(-1, new KeyValuePair<string, object?>(GrainTypeMetrics.TagName, metrics.GrainTypeTagValue));
    }

    internal void IncrementSystemTargetCounts(string systemTargetTypeName)
    {
        _systemTargetCounts.Add(1, new KeyValuePair<string, object?>("type", systemTargetTypeName));
    }

    internal void DecrementSystemTargetCounts(string systemTargetTypeName)
    {
        _systemTargetCounts.Add(-1, new KeyValuePair<string, object?>("type", systemTargetTypeName));
    }
}
