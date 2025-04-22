using Azure;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using System.Runtime.CompilerServices;
using Azure.Storage.Sas;
using Orleans.Serialization.Buffers;
using Microsoft.Extensions.Logging;

namespace Orleans.Journaling;

internal sealed partial class AzureAppendBlobLogStorage : IStateMachineStorage
{
    private static readonly AppendBlobCreateOptions CreateOptions = new() { Conditions = new() { IfNoneMatch = ETag.All } };
    private readonly AppendBlobClient _client;
    private readonly ILogger<AzureAppendBlobLogStorage> _logger;
    private readonly LogExtentBuilder.ReadOnlyStream _stream;
    private readonly AppendBlobAppendBlockOptions _appendOptions;
    private bool _exists;
    private int _numBlocks;

    public bool IsCompactionRequested => _numBlocks > 10;

    public AzureAppendBlobLogStorage(AppendBlobClient client, ILogger<AzureAppendBlobLogStorage> logger)
    {
        _client = client;
        _logger = logger;
        _stream = new();

        // For the first request, if we have not performed a read yet, we want to guard against clobbering an existing blob.
        _appendOptions = new AppendBlobAppendBlockOptions() { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } };
    }

    public async ValueTask AppendAsync(LogExtentBuilder value, CancellationToken cancellationToken)
    {
        if (!_exists)
        {
            var response = await _client.CreateAsync(CreateOptions, cancellationToken);
            _appendOptions.Conditions.IfNoneMatch = default;
            _appendOptions.Conditions.IfMatch = response.Value.ETag;
            _exists = true;
        }

        _stream.SetBuilder(value);
        var result = await _client.AppendBlockAsync(_stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogAppend(_logger, value.Length, _client.BlobContainerName, _client.Name);

        _stream.Reset();
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var conditions = new BlobRequestConditions { IfMatch = _appendOptions.Conditions.IfMatch };
        await _client.DeleteAsync(conditions: conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Expect no blob to have been created when we append to it.
        _appendOptions.Conditions.IfNoneMatch = ETag.All;
        _appendOptions.Conditions.IfMatch = default;
        _numBlocks = 0;
    }

    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Response<BlobDownloadStreamingResult> result;
        try
        {
            // If the blob was not newly created, then download the blob.
            result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            _exists = false;
            yield break;
        }

        // If the blob has a size of zero, check for a snapshot and restore the blob from the snapshot if one exists.
        if (result.Value.Details.ContentLength == 0)
        {
            if (result.Value.Details.Metadata.TryGetValue("snapshot", out var snapshot) && snapshot is { Length: > 0 })
            {
                result = await CopyFromSnapshotAsync(result.Value.Details.ETag, snapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        _numBlocks = result.Value.Details.BlobCommittedBlockCount;
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.Details.ETag;
        _exists = true;

        // Read everything into a single log segment. We could change this to read in chunks,
        // yielding when the stream does not return synchronously, if we wanted to support larger state machines.
        var rawStream = result.Value.Content;
        using var buffer = new ArcBufferWriter();
        while (true)
        {
            var mem = buffer.GetMemory();
            var bytesRead = await rawStream.ReadAsync(mem, cancellationToken);
            if (bytesRead == 0)
            {
                if (buffer.Length > 0)
                {
                    LogRead(_logger, buffer.Length, _client.BlobContainerName, _client.Name);
                    yield return new LogExtent(buffer.ConsumeSlice(buffer.Length));
                }

                yield break;
            }

            buffer.AdvanceWriter(bytesRead);
        }
    }

    private async Task<Response<BlobDownloadStreamingResult>> CopyFromSnapshotAsync(ETag eTag, string snapshotDetail, CancellationToken cancellationToken)
    {
        // Read snapshot and append it to the blob.
        var snapshot = _client.WithSnapshot(snapshotDetail);
        var uri = snapshot.GenerateSasUri(permissions: BlobSasPermissions.Read, expiresOn: DateTimeOffset.UtcNow.AddHours(1));
        var copyResult = await _client.SyncCopyFromUriAsync(
            uri,
            new BlobCopyFromUriOptions { DestinationConditions = new BlobRequestConditions { IfNoneMatch = eTag } },
            cancellationToken).ConfigureAwait(false);
        if (copyResult.Value.CopyStatus is not CopyStatus.Success)
        {
            throw new InvalidOperationException($"Copy did not complete successfully. Status: {copyResult.Value.CopyStatus}");
        }

        var result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _exists = true;
        return result;
    }

    public async ValueTask ReplaceAsync(LogExtentBuilder value, CancellationToken cancellationToken)
    {
        // Create a snapshot of the blob for recovery purposes.
        var blobSnapshot = await _client.CreateSnapshotAsync(conditions: _appendOptions.Conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Open the blob for writing, overwriting existing contents.
        var createOptions = new AppendBlobCreateOptions()
        {
            Conditions = _appendOptions.Conditions,
            Metadata = new Dictionary<string, string> { ["snapshot"] = blobSnapshot.Value.Snapshot },
        };
        var createResult = await _client.CreateAsync(createOptions, cancellationToken).ConfigureAwait(false);
        _appendOptions.Conditions.IfMatch = createResult.Value.ETag;
        _appendOptions.Conditions.IfNoneMatch = default;

        // Write the state machine snapshot.
        _stream.SetBuilder(value);
        var result = await _client.AppendBlockAsync(_stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogReplace(_logger, _client.BlobContainerName, _client.Name, value.Length);

        _stream.Reset();
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;

        // Delete the blob snapshot.
        await _client.WithSnapshot(blobSnapshot.Value.Snapshot).DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Appended {Length} bytes to blob \"{ContainerName}/{BlobName}\"")]
    private static partial void LogAppend(ILogger logger, long length, string containerName, string blobName);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Read {Length} bytes from blob \"{ContainerName}/{BlobName}\"")]
    private static partial void LogRead(ILogger logger, long length, string containerName, string blobName);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Replaced blob \"{ContainerName}/{BlobName}\", writing {Length} bytes")]
    private static partial void LogReplace(ILogger logger, string containerName, string blobName, long length);

}
