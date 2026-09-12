using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class GrainInstruments
{
    private readonly UpDownCounter<int> _grainCounts;
    private readonly UpDownCounter<int> _systemTargetCounts;
    internal ConcurrentDictionary<string, int> GrainCounts { get; } = new();

    public GrainInstruments(OrleansInstruments instruments)
    {
        _grainCounts = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.GRAIN_COUNTS);
        _systemTargetCounts = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.SYSTEM_TARGET_COUNTS);
    }

    internal void IncrementGrainCounts(string grainType, string grainTypeName)
    {
        GrainCounts.AddOrUpdate(grainTypeName, 1, static (_, count) => count + 1);
        _grainCounts.Add(1, new KeyValuePair<string, object?>(GrainTypeMetrics.TagName, grainType));
    }

    internal void DecrementGrainCounts(string grainType, string grainTypeName)
    {
        GrainCounts.AddOrUpdate(grainTypeName, -1, static (_, count) => count - 1);
        _grainCounts.Add(-1, new KeyValuePair<string, object?>(GrainTypeMetrics.TagName, grainType));
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
