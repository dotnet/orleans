namespace Orleans.Providers
{
    internal enum ProviderErrorCode
    {
        ProvidersBase = 200000,

        MemoryStreamProviderBase                    = ProvidersBase + 400,
        MemoryStreamProviderBase_QueueMessageBatchAsync = MemoryStreamProviderBase + 1,
        MemoryStreamProviderBase_GetQueueMessagesAsync = MemoryStreamProviderBase + 2,
    }
}
