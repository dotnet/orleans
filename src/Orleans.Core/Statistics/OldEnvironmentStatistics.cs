using System;

namespace Orleans.Statistics;

[Obsolete("Used only until the interfaces that it implements are removed from the codebase")]
internal sealed class OldEnvironmentStatistics(IEnvironmentStatisticsProvider statistics) : IAppEnvironmentStatistics, IHostEnvironmentStatistics
{
    public float? CpuUsage => statistics.GetEnvironmentStatistics().FilteredCpuUsagePercentage;
    public long? MemoryUsage => statistics.GetEnvironmentStatistics().FilteredMemoryUsageBytes;
    public long? AvailableMemory => statistics.GetEnvironmentStatistics().FilteredAvailableMemoryBytes;
    public long? TotalPhysicalMemory => statistics.GetEnvironmentStatistics().MaximumAvailableMemoryBytes;
}
