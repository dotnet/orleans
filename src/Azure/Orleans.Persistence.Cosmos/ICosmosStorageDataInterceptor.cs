namespace Orleans.Persistence.Cosmos
{
    public interface ICosmosStorageDataInterceptor
    {
        void BeforeCreateItem(ref PartitionKey partitionKey, object payload);
        void BeforeUpsertItem(ref PartitionKey partitionKey, object payload);
        void BeforeReplaceItem(ref PartitionKey partitionKey, object payload);
    }
}
