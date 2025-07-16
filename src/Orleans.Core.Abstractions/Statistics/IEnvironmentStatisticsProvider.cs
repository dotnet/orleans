using System;
using System.Runtime.InteropServices;
using Orleans.Serialization;
using System.Diagnostics;

namespace Orleans.Statistics;

/// <summary>
/// Provides statistics about the current process and its execution environment.
/// </summary>
public interface IEnvironmentStatisticsProvider
{
    /// <summary>
    /// Gets the current environment statistics. May apply filtering or processing based on the runtime configuration.
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
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct EnvironmentStatistics
{
    /// <summary>
    /// The system CPU usage.
    /// <br/>
    /// Applies Kalman filtering to smooth out short-term fluctuations.
    /// See <see href="https://en.wikipedia.org/wiki/Kalman_filter"/>;
    /// <see href="https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/"/>
    /// </summary>
    /// <remarks>Ranges from 0.0-100.0.</remarks>
    [Id(0)]
    public readonly float FilteredCpuUsagePercentage;

    /// <summary>
    /// The amount of managed memory currently consumed by the process.
    /// <br/>
    /// Applies Kalman filtering to smooth out short-term fluctuations.
    /// See <see href="https://en.wikipedia.org/wiki/Kalman_filter"/>;
    /// <see href="https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/"/>
    /// </summary>
    /// <remarks>
    /// Includes fragmented memory, which is the unused memory between objects on the managed heaps.
    /// </remarks>
    [Id(1)]
    public readonly long FilteredMemoryUsageBytes;

    /// <summary>
    /// The amount of memory currently available for allocations to the process.
    /// <br/>
    /// Applies Kalman filtering to smooth out short-term fluctuations.
    /// See <see href="https://en.wikipedia.org/wiki/Kalman_filter"/>;
    /// <see href="https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/"/>
    /// </summary>
    /// <remarks>
    /// Includes the currently available memory of the process and the system.
    /// </remarks>
    [Id(2)]
    public readonly long FilteredAvailableMemoryBytes;

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

    /// <summary>
    /// The system CPU usage.
    /// </summary>
    /// <remarks>Ranges from 0.0-100.0.</remarks>
    [Id(4)]
    public readonly float RawCpuUsagePercentage;

    /// <summary>
    /// The amount of managed memory currently consumed by the process.
    /// </summary>
    [Id(5)]
    public readonly long RawMemoryUsageBytes;

    /// <summary>
    /// The amount of memory currently available for allocations to the process.
    /// </summary>
    [Id(6)]
    public readonly long RawAvailableMemoryBytes;

    /// <summary>
    /// Gets the percentage of memory used relative to currently available memory, clamped between 0 and 100.
    /// </summary>
    public float MemoryUsagePercentage
    {
        get
        {
            if (MaximumAvailableMemoryBytes <= 0) return 0f;
            var percent = (double)RawMemoryUsageBytes / MaximumAvailableMemoryBytes * 100.0;
            return (float)Math.Clamp(percent, 0.0, 100.0);
        }
    }

    /// <summary>
    /// Gets the percentage of available memory relative to currently used memory, clamped between 0 and 100.
    /// </summary>
    /// <remarks>
    /// A value of <c>0</c> indicates that all available memory is currently in use.
    /// A value of <c>100</c> indicates that all memory is currently available.
    /// </remarks>
    public float AvailableMemoryPercentage
    {
        get
        {
            if (MaximumAvailableMemoryBytes <= 0) return 0f;
            var percent = (double)RawAvailableMemoryBytes / MaximumAvailableMemoryBytes * 100.0;
            return (float)Math.Clamp(percent, 0.0, 100.0);
        }
    }

    /// <summary>
    /// Gets the normalized memory usage (0.0 to 1.0).
    /// </summary>
    public float NormalizedMemoryUsage
    {
        get
        {
            if (MaximumAvailableMemoryBytes <= 0) return 0f;
            var fraction = (double)RawMemoryUsageBytes / MaximumAvailableMemoryBytes;
            return (float)Math.Clamp(fraction, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Gets the normalized filtered memory usage (0.0 to 1.0).
    /// </summary>
    public float NormalizedFilteredMemoryUsage
    {
        get
        {
            if (MaximumAvailableMemoryBytes <= 0) return 0f;
            var fraction = (double)FilteredMemoryUsageBytes / MaximumAvailableMemoryBytes;
            return (float)Math.Clamp(fraction, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Gets the normalized available memory (0.0 to 1.0).
    /// </summary>
    public float NormalizedAvailableMemory
    {
        get
        {
            if (MaximumAvailableMemoryBytes <= 0) return 0f;
            var fraction = (double)RawAvailableMemoryBytes / MaximumAvailableMemoryBytes;
            return (float)Math.Clamp(fraction, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Gets the normalized filtered available memory (0.0 to 1.0).
    /// </summary>
    public float NormalizedFilteredAvailableMemory
    {
        get
        {
            if (MaximumAvailableMemoryBytes <= 0) return 0f;
            var fraction = (double)FilteredAvailableMemoryBytes / MaximumAvailableMemoryBytes;
            return (float)Math.Clamp(fraction, 0.0, 1.0);
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB)
            return $"{bytes / (double)GB:F2} GB";
        if (bytes >= MB)
            return $"{bytes / (double)MB:F2} MB";
        if (bytes >= KB)
            return $"{bytes / (double)KB:F2} KB";
        return $"{bytes} B";
    }

    public override string ToString()
        => $"CpuUsage: {FilteredCpuUsagePercentage:F2}% (raw: {RawCpuUsagePercentage:F2}%) | " +
           $"MemoryUsage: {FormatBytes(FilteredMemoryUsageBytes)} (raw: {FormatBytes(RawMemoryUsageBytes)}) [{MemoryUsagePercentage:F2}%] | " +
           $"AvailableMemory: {FormatBytes(FilteredAvailableMemoryBytes)} (raw: {FormatBytes(RawAvailableMemoryBytes)}) [{AvailableMemoryPercentage:F2}%] | " +
           $"MaximumAvailableMemory: {FormatBytes(MaximumAvailableMemoryBytes)}";

    internal EnvironmentStatistics(
        float cpuUsagePercentage,
        float rawCpuUsagePercentage,
        long memoryUsageBytes,
        long rawMemoryUsageBytes,
        long availableMemoryBytes,
        long rawAvailableMemoryBytes,
        long maximumAvailableMemoryBytes)
    {
        FilteredCpuUsagePercentage = Math.Clamp(cpuUsagePercentage, 0f, 100f);
        RawCpuUsagePercentage = Math.Clamp(rawCpuUsagePercentage, 0f, 100f);
        FilteredMemoryUsageBytes = memoryUsageBytes;
        RawMemoryUsageBytes = rawMemoryUsageBytes;
        FilteredAvailableMemoryBytes = availableMemoryBytes;
        RawAvailableMemoryBytes = rawAvailableMemoryBytes;
        MaximumAvailableMemoryBytes = maximumAvailableMemoryBytes;

#if DEBUG
        Debug.Assert(MaximumAvailableMemoryBytes >= 0, $"{nameof(MaximumAvailableMemoryBytes)} must be non-negative. {this}");
        Debug.Assert(RawMemoryUsageBytes >= 0, $"{nameof(RawMemoryUsageBytes)} must be non-negative. {this}");
        Debug.Assert(RawAvailableMemoryBytes >= 0, $"{nameof(RawAvailableMemoryBytes)} must be non-negative. {this}");
        Debug.Assert(RawMemoryUsageBytes + RawAvailableMemoryBytes <= MaximumAvailableMemoryBytes,
            $"Sum of {nameof(RawMemoryUsageBytes)} and {nameof(RawAvailableMemoryBytes)} must not exceed {nameof(MaximumAvailableMemoryBytes)}. {this}");

        Debug.Assert(FilteredMemoryUsageBytes >= 0, $"{nameof(FilteredMemoryUsageBytes)} must be non-negative. {this}");
        Debug.Assert(FilteredAvailableMemoryBytes >= 0, $"{nameof(FilteredAvailableMemoryBytes)} must be non-negative. {this}");

        Debug.Assert(RawCpuUsagePercentage is >= 0.0f and <= 100.0f, $"{nameof(RawCpuUsagePercentage)} must be between 0.0 and 100.0. {this}");
        Debug.Assert(FilteredCpuUsagePercentage is >= 0.0f and <= 100.0f, $"{nameof(FilteredCpuUsagePercentage)} must be between 0.0 and 100.0. {this}");

        Debug.Assert(MemoryUsagePercentage is >= 0.0f and <= 100.0f, $"{nameof(MemoryUsagePercentage)} must be between 0.0 and 100.0. {this}");
        Debug.Assert(AvailableMemoryPercentage is >= 0.0f and <= 100.0f, $"{nameof(AvailableMemoryPercentage)} must be between 0.0 and 100.0. {this}");
        Debug.Assert(NormalizedMemoryUsage is >= 0.0f and <= 1.0f, $"{nameof(NormalizedMemoryUsage)} must be between 0.0 and 1.0. {this}");
        Debug.Assert(NormalizedAvailableMemory is >= 0.0f and <= 1.0f, $"{nameof(NormalizedAvailableMemory)} must be between 0.0 and 1.0. {this}");
        Debug.Assert(NormalizedFilteredMemoryUsage is >= 0.0f and <= 1.0f, $"{nameof(NormalizedFilteredMemoryUsage)} must be between 0.0 and 1.0. {this}");
        Debug.Assert(NormalizedFilteredAvailableMemory is >= 0.0f and <= 1.0f, $"{nameof(NormalizedFilteredAvailableMemory)} must be between 0.0 and 1.0. {this}");
#endif
    }
}
