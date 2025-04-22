using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// A default blob container factory that uses the default container name.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DefaultBlobContainerFactory"/> class.
/// </remarks>
/// <param name="options">The blob storage options</param>
internal sealed class DefaultBlobContainerFactory(
    IOptions<ClusterOptions> clusterOptions,
    AzureAppendBlobStateMachineStorageOptions options) : IBlobContainerFactory
{
    private BlobContainerClient _defaultContainer = null!;

    /// <inheritdoc/>
    public BlobContainerClient GetBlobContainerClient(GrainId grainId) => _defaultContainer;

    /// <inheritdoc/>
    public async Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
    {
        var prefix = clusterOptions.Value.ServiceId;
        _defaultContainer = client.GetBlobContainerClient($"{prefix}/{options.ContainerName}");
        await _defaultContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }
}
