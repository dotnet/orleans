namespace Orleans.Providers
{
    internal enum ProviderErrorCode
    {
        ProvidersBase = 200000,

        ShardedStorageProviderBase                  = ProvidersBase + 200,
        ShardedStorageProvider_ProviderName         = ShardedStorageProviderBase + 1,
        ShardedStorageProvider_HashValueOutOfBounds = ShardedStorageProviderBase + 2,

        MemoryStreamProviderBase                    = ProvidersBase + 400,
        MemoryStreamProviderBase_QueueMessageBatchAsync = MemoryStreamProviderBase + 1,
        MemoryStreamProviderBase_GetQueueMessagesAsync = MemoryStreamProviderBase + 2,
    }
}
