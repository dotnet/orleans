using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using static Orleans.Storage.StorageEntry;

namespace Orleans.Persistence.AzureStorage.Providers.Storage;

internal class AzureStorageTableEntryClient : StorageMigrationEntryClient
{
    /// <summary>
    /// table manager to use for keeping migration state info only
    /// </summary>
    private readonly AzureTableDataManager<TableEntity> tableManager;

    private readonly string partitionKey;
    private readonly string rowKey;

    public AzureStorageTableEntryClient(
        AzureTableDataManager<TableEntity> tableDataManager,
        TableEntity tableEntity)
    {
        this.tableManager = tableDataManager;

        this.partitionKey = tableEntity.PartitionKey;
        this.rowKey = tableEntity.RowKey;
    }

    public async ValueTask<DateTime?> GetEntryMigrationTimeAsync()
    {
        var (entity, etag) = await this.tableManager.ReadSingleTableEntryAsync(partitionKey, rowKey);
        if (entity is null && etag is null)
        {
            return null;
        }

        return entity.Timestamp.Value.DateTime;
    }

    public async Task MarkMigratedAsync(CancellationToken cancellationToken)
    {
        await this.tableManager.UpsertTableEntryAsync(new TableEntity(partitionKey, rowKey));
    }
}
