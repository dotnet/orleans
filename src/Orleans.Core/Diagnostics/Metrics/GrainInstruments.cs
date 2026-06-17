using System.Collections.Generic;
using System.Diagnostics.Metrics;

#nullable disable
namespace Orleans.Runtime;

internal sealed class GrainInstruments
{
    private readonly UpDownCounter<int> _grainCounts;
    private readonly UpDownCounter<int> _systemTargetCounts;

    public GrainInstruments(OrleansInstruments instruments)
    {
        GrainMetricsListener.Start();
        _grainCounts = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.GRAIN_COUNTS);
        _systemTargetCounts = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.SYSTEM_TARGET_COUNTS);
    }

    internal void IncrementGrainCounts(string grainTypeName)
    {
        _grainCounts.Add(1, new KeyValuePair<string, object>("type", grainTypeName));
    }

    internal void DecrementGrainCounts(string grainTypeName)
    {
        _grainCounts.Add(-1, new KeyValuePair<string, object>("type", grainTypeName));
    }

    internal void IncrementSystemTargetCounts(string systemTargetTypeName)
    {
        _systemTargetCounts.Add(1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }

    internal void DecrementSystemTargetCounts(string systemTargetTypeName)
    {
        _systemTargetCounts.Add(-1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
}
