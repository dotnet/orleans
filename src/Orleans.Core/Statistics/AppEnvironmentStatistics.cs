using System;

namespace Orleans.Statistics
{
    internal class AppEnvironmentStatistics : IAppEnvironmentStatistics
    {
        public long? MemoryUsage => GC.GetTotalMemory(false);
    }
}
