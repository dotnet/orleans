using Azure.Storage.Blobs;

namespace Orleans.Journaling;

/// <summary>
/// A default blob container factory that uses the default container name.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DefaultBlobContainerFactory"/> class.
/// </remarks>
/// <param name="options">The blob storage options</param>
internal sealed class DefaultBlobContainerFactory(AzureBlobJournalStorageOptions options) : IBlobContainerFactory
{
    private BlobContainerClient _defaultContainer = null!;

    /// <inheritdoc/>
    public BlobContainerClient GetBlobContainerClient(GrainId grainId) => _defaultContainer;

    /// <inheritdoc/>
    public BlobContainerClient GetBlobContainerClient(JournalId journalId) => _defaultContainer;

    /// <inheritdoc/>
    public async Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
    {
        _defaultContainer = client.GetBlobContainerClient(options.ContainerName);
        await _defaultContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }
}
