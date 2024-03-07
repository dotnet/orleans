using System;

namespace Orleans.Statistics;

[Obsolete("Used only until the interfaces that it implements are removed from the codebase")]
internal sealed class OldEnvironmentStatistics(IEnvironmentStatisticsProvider statistics) : IAppEnvironmentStatistics, IHostEnvironmentStatistics
{
    public float? CpuUsage => statistics.GetEnvironmentStatistics().CpuUsagePercentage;
    public long? MemoryUsage => statistics.GetEnvironmentStatistics().MemoryUsageBytes;
    public long? AvailableMemory => statistics.GetEnvironmentStatistics().AvailableMemoryBytes;
    public long? TotalPhysicalMemory => statistics.GetEnvironmentStatistics().MaximumAvailableMemoryBytes;
}
