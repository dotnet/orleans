using System;

namespace Orleans.Statistics
{
    internal class AppEnvironmentStatistics : IAppEnvironmentStatistics
    {
        public long? MemoryUsage =>
            GC.GetTotalMemory(false) +
            GC.GetGCMemoryInfo().FragmentedBytes; // add fragmented memory since `GetTotalMemory` does not account for it.
    }
}
