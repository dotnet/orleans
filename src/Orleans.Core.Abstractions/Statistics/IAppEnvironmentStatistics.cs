namespace Orleans.Statistics
{
    public interface IAppEnvironmentStatistics
    {
        long? MemoryUsage { get; }
    }
}