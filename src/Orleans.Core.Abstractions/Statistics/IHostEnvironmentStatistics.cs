namespace Orleans.Statistics
{
    public interface IHostEnvironmentStatistics
    {
        long? TotalPhysicalMemory { get; }

        float? CpuUsage { get; }

        long? AvailableMemory { get; }
    }
}
