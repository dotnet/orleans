using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class GrainInstruments
{
    static GrainInstruments()
    {
        GrainMetricsListener.Start();
    }

    internal static Counter<int> GrainCounts = Instruments.Meter.CreateCounter<int>(InstrumentNames.GRAIN_COUNTS);
    internal static void IncrementGrainCounts(string grainTypeName)
    {
        GrainCounts.Add(1, new KeyValuePair<string, object>("type", grainTypeName));
    }
    internal static void DecrementGrainCounts(string grainTypeName)
    {
        GrainCounts.Add(-1, new KeyValuePair<string, object>("type", grainTypeName));
    }

    internal static Counter<int> SystemTargetCounts = Instruments.Meter.CreateCounter<int>(InstrumentNames.SYSTEM_TARGET_COUNTS);
    internal static void IncrementSystemTargetCounts(string systemTargetTypeName)
    {
        SystemTargetCounts.Add(1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
    internal static void DecrementSystemTargetCounts(string systemTargetTypeName)
    {
        SystemTargetCounts.Add(-1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
}
