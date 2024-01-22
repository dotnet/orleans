using System;

namespace Orleans.Statistics;

[Obsolete("Used only until the interfaces that it implements are removed from the codebase")]
internal sealed class OldEnvironmentStatistics(IEnvironmentStatistics statistics) : IAppEnvironmentStatistics, IHostEnvironmentStatistics
{
    public float? CpuUsage => statistics.GetHardwareStatistics().CpuUsagePercentage;
    public long? MemoryUsage => statistics.GetHardwareStatistics().MemoryUsageBytes;
    public long? AvailableMemory => statistics.GetHardwareStatistics().AvailableMemoryBytes;
    public long? TotalPhysicalMemory => statistics.GetHardwareStatistics().MaximumAvailableMemoryBytes;
}
