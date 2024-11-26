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

    public bool IsMigratedEntry => _blob.Metadata.TryGetValue("isMigrated", out var migrated) && migrated == "true";

    public async Task MarkMigratedAsync(CancellationToken cancellationToken)
    {
        _blob.Metadata["isMigrated"] = "true";
        await _client.GetBlobClient(_blob.Name).SetMetadataAsync(_blob.Metadata, cancellationToken: cancellationToken);
    }
}
