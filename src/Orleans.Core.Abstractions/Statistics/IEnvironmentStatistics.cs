namespace Orleans.Statistics;

/// <summary>
/// Provides functionality for accessing statistics of the silo environment.
/// </summary>
public interface IEnvironmentStatistics
{
    /// <summary>
    /// Gets the system CPU usage.
    /// </summary>
    /// <remarks>Ranges from 0.0-100.0.</remarks>
    float CpuUsagePercentage { get; }

    /// <summary>
    /// Gets the currently occupied memory by the process.
    /// </summary>
    /// <remarks>Includes fragmented memory.</remarks>
    long MemoryUsageBytes { get; }

    /// <summary>
    /// Gets the currently available memory for allocations to the process.
    /// </summary>
    /// <remarks>
    /// Includes the currently available memory of the process, and the system.
    /// </remarks>
    long AvailableMemoryBytes { get; }

    /// <summary>
    /// Gets the maximum possible memory of the system.
    /// </summary>
    /// <remarks>Represents the physical memory, unless a lower-bound (typically in containers) has been specified.</remarks>
    long MaximumAvailableMemoryBytes { get; }
}
