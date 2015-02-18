namespace OrleansProviders.PersistentStream.MockQueueAdapter
{
    public interface IMockQueueAdapterSettings
    {
        int TotalQueueCount { get; }
        int CacheSizeKb { get; }
        int TargetBatchesSentPerSecond { get; }
    }
}