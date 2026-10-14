using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Orleans.Journaling.Ranges;

/// <summary>
/// Stores the range map as one JSON document in an Azure Storage blob.
/// </summary>
/// <remarks>
/// Each write reads the blob, checks its version, and uploads the new document conditionally on the ETag it read, so
/// a write succeeds only if no other write intervened. Each document carries a unique write id, so when the upload's
/// condition fails, possibly because the client retried an upload whose first attempt was applied, the store reads the
/// blob to determine whether this write was applied. If a later write has superseded the version this write targeted,
/// the outcome cannot be determined and the write throws <see cref="IOException"/>. The blob's container is created
/// on the first write if it does not exist.
/// </remarks>
public sealed class AzureBlobRangeMetaStore : IRangeMetaStore
{
    private readonly BlobClient _blob;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobRangeMetaStore"/> class.
    /// </summary>
    /// <param name="blob">The blob holding the range map.</param>
    public AzureBlobRangeMetaStore(BlobClient blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _blob = blob;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The blob does not hold a valid range map document.</exception>
    public async ValueTask<RangeMapSnapshot?> ReadAsync(CancellationToken cancellationToken)
        => (await ReadCoreAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The blob does not hold a valid range map document.</exception>
    /// <exception cref="IOException">The outcome of the write cannot be determined.</exception>
    public async ValueTask<RangeMapWriteResult> TryWriteAsync(RangeMap next, long expectedVersion, CancellationToken cancellationToken)
    {
        RangeMetaStoreArguments.Validate(next, expectedVersion);
        var (current, eTag, _) = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        if ((current?.Version ?? 0) != expectedVersion)
        {
            return new(false, current);
        }

        var written = new RangeMapSnapshot(expectedVersion + 1, next);

        // The write id makes this write's document unique, so a later read can tell whether this write was applied.
        var content = RangeMapJsonSerializer.Serialize(written, writeId: Guid.NewGuid().ToString("N"));
        var conditions = current is null
            ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : new BlobRequestConditions { IfMatch = eTag };
        try
        {
            await UploadAsync(content, conditions, cancellationToken).ConfigureAwait(false);
            return new(true, written);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // Another writer intervened, or the client retried an upload whose first attempt was applied.
            var (after, _, bytes) = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (after?.Version == expectedVersion + 1)
            {
                // Exactly one write produced this version: this one if the blob holds this write's document.
                return new(bytes.AsSpan().SequenceEqual(content), after);
            }

            if (after is not null && after.Version > expectedVersion + 1)
            {
                // This write may have been applied and then superseded.
                throw new IOException(
                    $"The outcome of writing range map version {expectedVersion + 1} is unknown: the blob now holds version {after.Version}. Read the map to continue.",
                    exception);
            }

            throw new IOException($"The range map blob changed unexpectedly while writing version {expectedVersion + 1}.", exception);
        }
    }

    private async Task UploadAsync(byte[] content, BlobRequestConditions conditions, CancellationToken cancellationToken)
    {
        var options = new BlobUploadOptions
        {
            Conditions = conditions,
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        };
        try
        {
            await _blob.UploadAsync(new BinaryData(content), options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            await _blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await _blob.UploadAsync(new BinaryData(content), options, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(RangeMapSnapshot? Snapshot, ETag ETag, byte[]? Content)> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            var content = response.Value.Content.ToArray();
            return (RangeMapJsonSerializer.Deserialize(content), response.Value.Details.ETag, content);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return (null, default, null);
        }
    }
}
