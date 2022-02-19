namespace Orleans.Statistics
{
    /// <summary>
    /// Provides functionality for accessing statistics relating to the application environment.
    /// </summary>
    public interface IAppEnvironmentStatistics
    {
        /// <summary>
        /// Gets the total memory usage, in bytes, if available.
        /// </summary>
        long? MemoryUsage { get; }
    }
}