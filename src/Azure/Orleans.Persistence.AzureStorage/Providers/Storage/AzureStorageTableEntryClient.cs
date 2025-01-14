using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using static Orleans.Storage.StorageEntry;

namespace Orleans.Persistence.AzureStorage.Providers.Storage;

internal class AzureStorageTableEntryClient : StorageMigrationEntryClient
{
    /// <summary>
    /// table manager - allows to update the entry if needed
    /// </summary>
    private readonly AzureTableDataManager<TableEntity> tableManager;

    /// <summary>
    /// Entry in the table itself. Used to lookup for entry properties
    /// </summary>
    private readonly TableEntity tableEntity;

    public AzureStorageTableEntryClient(
        AzureTableDataManager<TableEntity> tableDataManager,
        TableEntity tableEntity)
    {
        this.tableManager = tableDataManager;
        this.tableEntity = tableEntity;
    }

    public DateTime? EntryMigrationTime
    {
        get
        {
            if (tableEntity.TryGetValue("migrationTime", out object migrationTime))
            {
                return DateTime.TryParse(migrationTime.ToString(), out DateTime result) ? result : null;
            }

            return null;
        }
    }

    public Task MarkMigratedAsync(CancellationToken cancellationToken)
    {
        tableEntity["migrationTime"] = DateTime.UtcNow.ToString();
        return tableManager.UpdateTableEntryAsync(tableEntity, tableEntity.ETag);
    }
}
