using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class MiscInstruments
{
    internal static Counter<int> GrainCounts = Instruments.Meter.CreateCounter<int>(StatisticNames.GRAIN_COUNTS);
    internal static void IncrementGrainCounts(string grainTypeName)
    {
        GrainCounts.Add(1, new KeyValuePair<string, object>("type", grainTypeName));
    }
    internal static void DecrementGrainCounts(string grainTypeName)
    {
        GrainCounts.Add(-1, new KeyValuePair<string, object>("type", grainTypeName));
    }

    internal static Counter<int> SystemTargetCounts = Instruments.Meter.CreateCounter<int>(StatisticNames.SYSTEM_TARGET_COUNTS);
    internal static void IncrementSystemTargetCounts(string systemTargetTypeName)
    {
        SystemTargetCounts.Add(1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
    internal static void DecrementSystemTargetCounts(string systemTargetTypeName)
    {
        SystemTargetCounts.Add(-1, new KeyValuePair<string, object>("type", systemTargetTypeName));
    }
}
