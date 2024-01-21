using System;

namespace Orleans.Statistics;

[Obsolete("Used only until the interfaces that it implements are removed from the codebase")]
internal sealed class OldEnvironmentStatistics(IEnvironmentStatistics statistics) : IAppEnvironmentStatistics, IHostEnvironmentStatistics
{
    public float? CpuUsage => statistics.CpuUsagePercentage;
    public long? MemoryUsage => statistics.MemoryUsageBytes;
    public long? AvailableMemory => statistics.AvailableMemoryBytes;
    public long? TotalPhysicalMemory => statistics.MaximumAvailableMemoryBytes;
}
