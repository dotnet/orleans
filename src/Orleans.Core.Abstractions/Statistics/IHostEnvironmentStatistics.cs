namespace Orleans.Statistics
{
    /// <summary>
    /// Functionality for accessing statistics relating to the hosting environment.
    /// </summary>
    public interface IHostEnvironmentStatistics
    {
        /// <summary>
        /// Gets the total physical memory on the host in bytes.
        /// </summary>
        /// <example>
        /// <c>16426476000L</c> for 16 GB.
        /// </example>
        long? TotalPhysicalMemory { get; }

        /// <summary>
        /// Gets the host CPU usage from 0.0-100.0.
        /// </summary>
        /// <example>
        /// <c>70.0f</c> for 70% CPU usage.
        /// </example>
        float? CpuUsage { get; }

        /// <summary>
        /// Gets the total memory available for allocation on the host in bytes.
        /// </summary>
        /// <example>
        /// <c>14426476000L</c> for 14 GB.
        /// </example>
        long? AvailableMemory { get; }
    }
}
