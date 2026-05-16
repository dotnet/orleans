using Azure.Storage.Blobs;

namespace Orleans.Journaling;

/// <summary>
/// A factory for building container clients for blob storage.
/// </summary>
public interface IBlobContainerFactory
{
    /// <summary>
    /// Gets the container which should be used for the specified journal.
    /// </summary>
    /// <param name="journalId">The journal id.</param>
    /// <returns>A configured blob client.</returns>
    public BlobContainerClient GetBlobContainerClient(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        throw new NotSupportedException(
            $"This blob container factory does not support grain-independent journals. Implement {nameof(GetBlobContainerClient)}({nameof(JournalId)}) to support on-demand journals.");
    }

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
