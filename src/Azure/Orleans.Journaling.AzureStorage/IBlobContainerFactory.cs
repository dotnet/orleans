using Azure.Storage.Blobs;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// A factory for building container clients for blob storage using GrainId
/// </summary>
public interface IBlobContainerFactory
{
    /// <summary>
    /// Gets the container which should be used for the specified grain.
    /// </summary>
    /// <param name="grainId">The grain id</param>
    /// <returns>A configured blob client</returns>
    public BlobContainerClient GetBlobContainerClient(GrainId grainId);

    /// <summary>
    /// Initialize any required dependencies using the provided client and options.
    /// </summary>
    /// <param name="client">The connected blob client</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken);
}
