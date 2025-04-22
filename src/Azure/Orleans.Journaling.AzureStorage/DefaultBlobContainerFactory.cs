using Azure.Storage.Blobs;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// A default blob container factory that uses the default container name.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DefaultBlobContainerFactory"/> class.
/// </remarks>
/// <param name="options">The blob storage options</param>
internal sealed class DefaultBlobContainerFactory(AzureAppendBlobStateMachineStorageOptions options) : IBlobContainerFactory
{
    private BlobContainerClient _defaultContainer = null!;

    /// <inheritdoc/>
    public BlobContainerClient GetBlobContainerClient(GrainId grainId) => _defaultContainer;

    /// <inheritdoc/>
    public async Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
    {
        _defaultContainer = client.GetBlobContainerClient(options.ContainerName);
        await _defaultContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }
}
