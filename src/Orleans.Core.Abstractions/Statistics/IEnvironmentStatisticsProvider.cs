using System;
using System.Runtime.InteropServices;

namespace Orleans.Statistics;

/// <summary>
/// Provides statistics about the current process and its execution environment.
/// </summary>
public interface IEnvironmentStatisticsProvider
{
    /// <summary>
    /// Gets the current environment statistics.
    /// </summary>
    EnvironmentStatistics GetEnvironmentStatistics();
}

// This struct is intentionally 'packed' in order to avoid extra padding.
// This will be created very frequently, so we reduce stack size and lower the serialization cost.
// As more fields are added to this, they could be placed in such a manner that it may result in a lot of 'empty' space.
/// <summary>
/// Contains statistics about the current process and its execution environment.
/// </summary>
[Immutable]
[GenerateSerializer]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[Alias("Orleans.Statistics.EnvironmentStatistics")]
public readonly struct EnvironmentStatistics
{
    /// <summary>
    /// The system CPU usage.
    /// </summary>
    /// <remarks>Ranges from 0.0-100.0.</remarks>
    [Id(0)]
    public readonly float CpuUsagePercentage;

    /// <summary>
    /// The amount of managed memory currently consumed by the process.
    /// </summary>
    /// <remarks>Includes fragmented memory, which is the unused memory between objects on the managed heaps.</remarks>
    [Id(1)]
    public readonly long MemoryUsageBytes;

    /// <summary>
    /// The amount of memory currently available for allocations to the process.
    /// </summary>
    /// <remarks>
    /// Includes the currently available memory of the process and the system.
    /// </remarks>
    [Id(2)]
    public readonly long AvailableMemoryBytes;

    /// <summary>
    /// The maximum amount of memory available to the process.
    /// </summary>
    /// <remarks>
    /// This value is computed as the lower of two amounts:
    /// <list type="bullet">
    ///   <item><description>The amount of memory after which the garbage collector will begin aggressively collecting memory, defined by <see cref="GCMemoryInfo.HighMemoryLoadThresholdBytes"/>.</description></item>
    ///   <item><description>The process' configured memory limit, defined by <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/>.</description></item>
    /// </list>
    /// Memory limits are common in containerized environments. For more information on configuring memory limits, see <see href="https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-limit"/>
    /// </remarks>
    [Id(3)]
    public readonly long MaximumAvailableMemoryBytes;

    internal EnvironmentStatistics(float cpuUsagePercentage, long memoryUsageBytes, long availableMemoryBytes, long maximumAvailableMemoryBytes)
    {
        CpuUsagePercentage = cpuUsagePercentage;
        MemoryUsageBytes = memoryUsageBytes;
        AvailableMemoryBytes = availableMemoryBytes;
        MaximumAvailableMemoryBytes = maximumAvailableMemoryBytes;
    }
}