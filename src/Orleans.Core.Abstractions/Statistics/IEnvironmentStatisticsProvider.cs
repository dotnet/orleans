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

/// <summary>
/// Contains statistics about the current process and its execution environment.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct EnvironmentStatistics
{
    /// <summary>
    /// The system CPU usage.
    /// </summary>
    /// <remarks>Ranges from 0.0-100.0.</remarks>
    public readonly float CpuUsagePercentage;

    /// <summary>
    /// The amount of managed memory currently consumed by the process.
    /// </summary>
    /// <remarks>Includes fragmented memory, which is the unused memory between objects on the managed heaps.</remarks>
    public readonly long MemoryUsageBytes;

    /// <summary>
    /// The amount of memory currently available for allocations to the process.
    /// </summary>
    /// <remarks>
    /// Includes the currently available memory of the process and the system.
    /// </remarks>
    public readonly long AvailableMemoryBytes;

    /// <summary>
    /// The maximum amount of memory available to the process.
    /// </summary>
    /// <remarks>
    /// This value is computed as the lower of two amounts:
    /// <list type="bullet">
    ///   <item>The amount of memory after which the garbage collector will begin aggressively collecting memory, defined by <see cref="GCMemoryInfo.HighMemoryLoadThresholdBytes"/>.</item>
    ///   <item>The process' configured memory limit, defined by <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/>.</item>
    /// </list>
    /// Memory limits are common in containerized environments. For more information on configuring memory limits, see <see href="https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-limit"/>
    /// </remarks>
    public readonly long MaximumAvailableMemoryBytes;

    internal EnvironmentStatistics(float cpuUsagePercentage, long memoryUsageBytes, long availableMemoryBytes, long maximumAvailableMemoryBytes)
    {
        CpuUsagePercentage = cpuUsagePercentage;
        MemoryUsageBytes = memoryUsageBytes;
        AvailableMemoryBytes = availableMemoryBytes;
        MaximumAvailableMemoryBytes = maximumAvailableMemoryBytes;
    }
}