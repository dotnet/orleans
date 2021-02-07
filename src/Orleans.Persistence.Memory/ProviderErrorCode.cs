namespace Orleans.Providers
{
    internal enum ProviderErrorCode
    {
        ProvidersBase = 200000,

        ShardedStorageProviderBase                  = ProvidersBase + 200,
        ShardedStorageProvider_ProviderName         = ShardedStorageProviderBase + 1,
        ShardedStorageProvider_HashValueOutOfBounds = ShardedStorageProviderBase + 2,
    }
}
