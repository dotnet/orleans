using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using static Orleans.Storage.StorageEntry;

namespace Orleans.Persistence.AzureStorage.Providers.Storage;

internal class AzureStorageBlobEntryClient : StorageMigrationEntryClient
{
    private readonly BlobContainerClient _client;

    private readonly BlobItem _blobItem;
    private readonly BlobClient _blobClient;

    public AzureStorageBlobEntryClient(BlobContainerClient client, BlobItem blob)
    {
        _client = client;
        _blobItem = blob;
    }

    public AzureStorageBlobEntryClient(BlobClient blobClient)
    {
        _blobClient = blobClient;
    }

    public async ValueTask<DateTime?> GetEntryMigrationTimeAsync()
    {
        IDictionary<string, string> metadata;
        if (_blobItem is not null)
        {
            metadata = _blobItem.Metadata;
        }
        else
        {
            var props = await _blobClient.GetPropertiesAsync();
            metadata = props?.Value?.Metadata;
        }
        
        if (metadata is null || !metadata.TryGetValue("migrationTime", out var migrationTime))
        {
            return null;
        }

        DateTime? result = DateTime.TryParse(migrationTime, out var time) ? time : null;
        return result;
    }

    public async Task<string> MarkMigratedAsync(CancellationToken cancellationToken)
    {
        IDictionary<string, string> metadata;
        if (_blobItem is not null)
        {
            metadata = _blobItem.Metadata;
        }
        else
        {
            var props = await _blobClient.GetPropertiesAsync();
            metadata = props?.Value?.Metadata;
        }

        if (metadata is null)
        {
            metadata = new Dictionary<string, string>();
        }

        metadata["migrationTime"] = DateTime.UtcNow.ToString();

        BlobClient blobClient = _blobClient ?? _client.GetBlobClient(_blobItem.Name);
        var response = await blobClient.SetMetadataAsync(metadata, cancellationToken: cancellationToken);

        // need to return an updated ETag of the original entry,
        // because metadata is set on original entry directly
        return response?.Value?.ETag.ToString() ?? null;
    }
}
