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
    public BlobContainerClient GetBlobContainerClient(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return _defaultContainer;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
        => InitializeAsync(client, cancellationToken, AzureBlobJournalStorageInstruments.CreateForDirectConstruction());

    internal async Task InitializeAsync(
        BlobServiceClient client, CancellationToken cancellationToken, AzureBlobJournalStorageInstruments instruments)
    {
        _defaultContainer = client.GetBlobContainerClient(options.ContainerName);
        await instruments.TrackApiCallAsync(
            nameof(BlobContainerClient.CreateIfNotExistsAsync),
            () => _defaultContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken),
            static result => result is null ? JournalStorageTelemetry.AlreadyExists
                : JournalStorageTelemetry.GetHttpStatus(result.GetRawResponse().Status)).ConfigureAwait(false);
    }
}
