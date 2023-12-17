using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class GrainInstruments
{
    static GrainInstruments()
    {
        GrainMetricsListener.Start();
    }

    internal static UpDownCounter<int> GrainCounts = Instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.GRAIN_COUNTS);
    internal static void IncrementGrainCounts(string grainTypeName)
    {
        GrainCounts.Add(1, new KeyValuePair<string, object>("type", grainTypeName));
    }
    internal static void DecrementGrainCounts(string grainTypeName)
    {
        GrainCounts.Add(-1, new KeyValuePair<string, object>("type", grainTypeName));
    }

    internal static UpDownCounter<int> SystemTargetCounts = Instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.SYSTEM_TARGET_COUNTS);
    internal static void IncrementSystemTargetCounts(string systemTargetTypeName)
    {
        SystemTargetCounts.Add(1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
    internal static void DecrementSystemTargetCounts(string systemTargetTypeName)
    {
        SystemTargetCounts.Add(-1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
}
