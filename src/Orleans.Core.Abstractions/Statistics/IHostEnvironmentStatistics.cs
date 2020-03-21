namespace Orleans.Statistics
{
    public interface IHostEnvironmentStatistics
    {
        /// <summary>
        /// Total physical memory on the host in bytes
        /// i.e. 16426476000L for 16 gb
        /// </summary>
        /// <value>16426476000</value>
        long? TotalPhysicalMemory { get; }

        /// <summary>
        /// Host CPU usage from 0.0-1.0
        /// i.e. 0.7f for 70% CPU usage
        /// </summary>
        /// <value>0.7</value>
        float? CpuUsage { get; }

        /// <summary>
        /// Total memory available for allocation on the host in bytes
        /// i.e. 14426476000L for 14 gb
        /// </summary>
        /// <value>14426476000</value>
        long? AvailableMemory { get; }
    }
}
