using System.Runtime.InteropServices;

namespace Orleans.Statistics;

public interface IEnvironmentStatisticsProvider
{
    /// <summary>
    /// Gets the statistics of the silo environment.
    /// </summary>
    EnvironmentStatistics GetEnvironmentStatistics();
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct EnvironmentStatistics
{
    /// <summary>
    /// The system CPU usage.
    /// </summary>
    /// <remarks>Ranges from 0.0-100.0.</remarks>
    public readonly float CpuUsagePercentage;

    /// <summary>
    /// The currently occupied memory by the process.
    /// </summary>
    /// <remarks>Includes fragmented memory.</remarks>
    public readonly long MemoryUsageBytes;

    /// <summary>
    /// The currently available memory for allocations to the process.
    /// </summary>
    /// <remarks>
    /// Includes the currently available memory of the process, and the system.
    /// </remarks>
    public readonly long AvailableMemoryBytes;

    /// <summary>
    /// The maximum possible memory of the system.
    /// </summary>
    /// <remarks>Represents the physical memory, unless a lower-bound (typically in containers) has been specified.</remarks>
    public readonly long MaximumAvailableMemoryBytes;

    internal EnvironmentStatistics(float cpuUsagePercentage, long memoryUsageBytes, long availableMemoryBytes, long maximumAvailableMemoryBytes)
    {
        CpuUsagePercentage = cpuUsagePercentage;
        MemoryUsageBytes = memoryUsageBytes;
        AvailableMemoryBytes = availableMemoryBytes;
        MaximumAvailableMemoryBytes = maximumAvailableMemoryBytes;
    }
}