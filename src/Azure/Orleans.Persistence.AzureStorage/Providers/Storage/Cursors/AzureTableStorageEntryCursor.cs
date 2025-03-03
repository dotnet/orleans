using System.ComponentModel.Design.Serialization;

namespace Orleans.Persistence.AzureStorage.Providers.Storage.Cursors
{
    internal class AzureTableStorageEntryCursor
    {
        public string PartitionKey { get; }
        public string RowKey { get; }

        public AzureTableStorageEntryCursor(string partitionKey, string rowKey)
        {
            PartitionKey = partitionKey;
            RowKey = rowKey;
        }

        public override string ToString() => $"PK={PartitionKey};RK={RowKey}";
    }
}
