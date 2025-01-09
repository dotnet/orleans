using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using static Orleans.Storage.StorageEntry;

namespace Orleans.Persistence.AzureStorage.Providers.Storage;

internal class AzureStorageBlobEntryClient : StorageMigrationEntryClient
{
    private readonly BlobContainerClient _client;
    private readonly BlobItem _blob;

    public AzureStorageBlobEntryClient(BlobContainerClient client, BlobItem blob)
    {
        _client = client;
        _blob = blob;
    }

    public DateTime? EntryMigrationTime
    {
        get
        {
            if (!_blob.Metadata.TryGetValue("migrationTime", out var migrationTime))
            {
                return null;
            }

            return DateTime.TryParse(migrationTime, out var time) ? time : null;
        }
    }

    public async Task MarkMigratedAsync(CancellationToken cancellationToken)
    {
        _blob.Metadata["migrationTime"] = DateTime.UtcNow.ToString();
        await _client.GetBlobClient(_blob.Name).SetMetadataAsync(_blob.Metadata, cancellationToken: cancellationToken);
    }
}
