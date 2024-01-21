using System;

namespace Orleans.Statistics
{
    /// <summary>
    /// Provides functionality for accessing statistics relating to the application environment.
    /// </summary>
    [Obsolete($"This functionality will be removed, use {nameof(IEnvironmentStatistics)} instead.", error: true)]
    public interface IAppEnvironmentStatistics
    {
        /// <summary>
        /// Gets the total memory usage, in bytes, if available.
        /// </summary>
        long? MemoryUsage { get; }
    }
}