using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Storage;

/// <summary>
/// A factory for building container clients for blob storage using grainType and grainId
/// </summary>
public interface IBlobContainerFactory
{
    /// <summary>
    /// Build a container for the specific grain type and grain id
    /// </summary>
    /// <param name="grainType">The grain type</param>
    /// <param name="grainId">The grain id</param>
    /// <returns>A configured blob client</returns>
    public BlobContainerClient BuildContainerClient(string grainType, GrainReference grainId);

    /// <summary>
    /// Initialize any required dependencies using the provided client and options
    /// </summary>
    /// <param name="client">The connected blob client</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task Init(BlobServiceClient client);
}

/// <summary>
/// A default blob container factory that uses the default container name
/// </summary>
internal class DefaultBlobContainerFactory : IBlobContainerFactory
{
    private readonly AzureBlobStorageOptions _options;
    private BlobContainerClient _defaultContainer = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultBlobContainerFactory"/> class.
    /// </summary>
    /// <param name="options">The blob storage options</param>
    public DefaultBlobContainerFactory(AzureBlobStorageOptions options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public BlobContainerClient BuildContainerClient(string grainType, GrainReference grainId)
        => _defaultContainer;

    /// <inheritdoc/>
    public async Task Init(BlobServiceClient client)
    {
        _defaultContainer = client.GetBlobContainerClient(_options.ContainerName);
        await _defaultContainer.CreateIfNotExistsAsync();
    }
}