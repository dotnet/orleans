namespace Orleans.SqlUtils.StorageProvider
{
    internal sealed class BatchingOptions
    {
        public int BatchSize { get; set; }
        public int MaxConcurrentWrites { get; set; }
        public int BatchTimeoutSeconds { get; set; }
    }
}