using System.Buffers;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace Orleans.Journaling;

internal sealed partial class AzureAppendBlobJournalStorage : IJournalStorage
{
    internal const string FormatKeyMetadataKey = "journalFormatKey";

    // Azure Append Blob hard limits documented at:
    // https://learn.microsoft.com/azure/storage/blobs/scalability-targets#scale-targets-for-blob-storage
    // The append-block size cap is 100 MiB for service version >= 2022-11-02.
    internal const long MaxAppendBlockBytes = 100L * 1024 * 1024;

    // An append blob can hold up to 50,000 committed blocks. The compaction request
    // (IsCompactionRequested) trips far earlier (at 10 blocks), so this guard exists only
    // to fail fast if a consumer ignores compaction requests for an extended period.
    internal const int MaxAppendBlobBlocks = 50_000;
    private const int AppendBlobBlockCeilingHeadroom = 100;

    private readonly AppendBlobClient _client;
    private readonly string? _mimeType;
    private readonly string? _journalFormatKey;
    private readonly ILogger<AzureAppendBlobJournalStorage> _logger;
    private readonly Func<AppendBlobClient, string, AppendBlobClient> _snapshotClientFactory;
    private readonly Func<AppendBlobClient, CancellationToken, IAsyncEnumerable<string>> _snapshotEnumerator;
    private readonly AppendBlobAppendBlockOptions _appendOptions;
    private bool _exists;
    private int _numBlocks;

    public bool IsCompactionRequested => _numBlocks > 10;

    public AzureAppendBlobJournalStorage(AppendBlobClient client, ILogger<AzureAppendBlobJournalStorage> logger)
        : this(client, mimeType: null, logger)
    {
    }

    internal AzureAppendBlobJournalStorage(AppendBlobClient client, ILogger<AzureAppendBlobJournalStorage> logger, Func<AppendBlobClient, string, AppendBlobClient> snapshotClientFactory)
        : this(client, mimeType: null, logger, snapshotClientFactory)
    {
    }

    internal AzureAppendBlobJournalStorage(AppendBlobClient client, string? mimeType, ILogger<AzureAppendBlobJournalStorage> logger)
        : this(client, mimeType, logger, static (client, snapshot) => client.WithSnapshot(snapshot))
    {
    }

    internal AzureAppendBlobJournalStorage(
        AppendBlobClient client,
        string? mimeType,
        ILogger<AzureAppendBlobJournalStorage> logger,
        Func<AppendBlobClient, string, AppendBlobClient> snapshotClientFactory,
        Func<AppendBlobClient, CancellationToken, IAsyncEnumerable<string>>? snapshotEnumerator = null,
        string? journalFormatKey = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(snapshotClientFactory);

        _client = client;
        _mimeType = mimeType;
        _journalFormatKey = journalFormatKey;
        _logger = logger;
        _snapshotClientFactory = snapshotClientFactory;
        _snapshotEnumerator = snapshotEnumerator ?? DefaultEnumerateSnapshots;

        // For the first request, if we have not performed a read yet, we want to guard against clobbering an existing blob.
        _appendOptions = new AppendBlobAppendBlockOptions() { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } };
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        ThrowIfBatchTooLarge(value.Length, isReplace: false);
        ThrowIfBlockCeilingApproached();

        if (!_exists)
        {
            var response = await _client.CreateAsync(CreateOptions(new AppendBlobRequestConditions { IfNoneMatch = ETag.All }), cancellationToken);
            _appendOptions.Conditions.IfNoneMatch = default;
            _appendOptions.Conditions.IfMatch = response.Value.ETag;
            _exists = true;
        }

        using var stream = new ReadOnlySequenceStream(value);
        var result = await _client.AppendBlockAsync(stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogAppend(_logger, stream.Length, _client.BlobContainerName, _client.Name);

        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;
    }

    private void ThrowIfBatchTooLarge(long length, bool isReplace)
    {
        if (length <= MaxAppendBlockBytes)
        {
            return;
        }

        var operation = isReplace ? "snapshot" : "journal batch";
        throw new InvalidOperationException(
            $"Azure Append Blob {operation} of {length:N0} bytes exceeds the per-block limit of {MaxAppendBlockBytes:N0} bytes (100 MiB). " +
            "Reduce the snapshot/operation size or compact more aggressively.");
    }

    private void ThrowIfBlockCeilingApproached()
    {
        if (_numBlocks < MaxAppendBlobBlocks - AppendBlobBlockCeilingHeadroom)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Azure Append Blob '{_client.Name}' in container '{_client.BlobContainerName}' has reached {_numBlocks:N0} of the maximum {MaxAppendBlobBlocks:N0} committed blocks. " +
            "Compaction must run (IsCompactionRequested has been signaled at 11 blocks). Refusing to append further to avoid hitting the hard Azure limit.");
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var conditions = new BlobRequestConditions { IfMatch = _appendOptions.Conditions.IfMatch };
        await _client.DeleteAsync(conditions: conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Expect no blob to have been created when we append to it.
        _appendOptions.Conditions.IfNoneMatch = ETag.All;
        _appendOptions.Conditions.IfMatch = default;
        _exists = false;
        _numBlocks = 0;
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        Response<BlobDownloadStreamingResult> result;
        try
        {
            result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            _exists = false;
            consumer.Complete();
            return;
        }

        ValidateFormatKeyMetadata(result.Value.Details.Metadata);

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

        await using (var rawStream = result.Value.Content)
        {
            var totalBytesRead = await consumer.ConsumeAsync(rawStream, cancellationToken).ConfigureAwait(false);
            LogRead(_logger, totalBytesRead, _client.BlobContainerName, _client.Name);
        }

        // Best-effort cleanup of orphan snapshots left behind by previously-interrupted ReplaceAsync calls.
        // After a successful read, no further operation depends on any pre-existing snapshot, so we can
        // safely delete them. Failures here do not impact recovery and are only logged.
        await TryCleanupOrphanSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryCleanupOrphanSnapshotsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in _snapshotEnumerator(_client, cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (snapshot is not { Length: > 0 })
                {
                    continue;
                }

                try
                {
                    await _snapshotClientFactory(_client, snapshot)
                        .DeleteAsync(conditions: null, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    LogOrphanSnapshotDeleted(_logger, _client.BlobContainerName, _client.Name, snapshot);
                }
                catch (RequestFailedException ex) when (ex.Status is 404)
                {
                    // Snapshot was already gone; nothing to do.
                }
                catch (Exception ex)
                {
                    LogOrphanSnapshotDeleteFailed(_logger, ex, _client.BlobContainerName, _client.Name, snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogOrphanSnapshotEnumerationFailed(_logger, ex, _client.BlobContainerName, _client.Name);
        }
    }

    private static async IAsyncEnumerable<string> DefaultEnumerateSnapshots(
        AppendBlobClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var container = client.GetParentBlobContainerClient();
        await foreach (var item in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.Snapshots,
            prefix: client.Name,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (item.Snapshot is { Length: > 0 } snapshot
                && string.Equals(item.Name, client.Name, StringComparison.Ordinal))
            {
                yield return snapshot;
            }
        }
    }

    private async Task<Response<BlobDownloadStreamingResult>> CopyFromSnapshotAsync(ETag eTag, string snapshotDetail, CancellationToken cancellationToken)
    {
        // Read snapshot and append it to the blob.
        var snapshot = _snapshotClientFactory(_client, snapshotDetail);
        var uri = snapshot.GenerateSasUri(permissions: BlobSasPermissions.Read, expiresOn: DateTimeOffset.UtcNow.AddHours(1));
        var copyResult = await _client.SyncCopyFromUriAsync(
            uri,
            new BlobCopyFromUriOptions { DestinationConditions = new BlobRequestConditions { IfMatch = eTag } },
            cancellationToken).ConfigureAwait(false);
        if (copyResult.Value.CopyStatus is not CopyStatus.Success)
        {
            throw new InvalidOperationException($"Copy did not complete successfully. Status: {copyResult.Value.CopyStatus}");
        }

        var result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _exists = true;
        return result;
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        ThrowIfBatchTooLarge(value.Length, isReplace: true);

        // Create a snapshot of the blob for recovery purposes.
        var blobSnapshot = await _client.CreateSnapshotAsync(conditions: _appendOptions.Conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Open the blob for writing, overwriting existing contents.
        var metadata = new Dictionary<string, string> { ["snapshot"] = blobSnapshot.Value.Snapshot };
        if (_journalFormatKey is { Length: > 0 })
        {
            metadata[FormatKeyMetadataKey] = _journalFormatKey;
        }

        var createOptions = new AppendBlobCreateOptions()
        {
            Conditions = new AppendBlobRequestConditions
            {
                IfMatch = _appendOptions.Conditions.IfMatch,
                IfNoneMatch = _appendOptions.Conditions.IfNoneMatch,
            },
            Metadata = metadata,
            HttpHeaders = CreateHttpHeaders(),
        };
        var createResult = await _client.CreateAsync(createOptions, cancellationToken).ConfigureAwait(false);
        _appendOptions.Conditions.IfMatch = createResult.Value.ETag;
        _appendOptions.Conditions.IfNoneMatch = default;

        // Write the state snapshot.
        using var stream = new ReadOnlySequenceStream(value);
        var result = await _client.AppendBlockAsync(stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogReplace(_logger, _client.BlobContainerName, _client.Name, stream.Length);

        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;

        // Delete the blob snapshot.
        await _snapshotClientFactory(_client, blobSnapshot.Value.Snapshot).DeleteAsync(conditions: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private AppendBlobCreateOptions CreateOptions(AppendBlobRequestConditions conditions) => new()
    {
        Conditions = conditions,
        HttpHeaders = CreateHttpHeaders(),
        Metadata = CreateMetadata(),
    };

    private BlobHttpHeaders? CreateHttpHeaders() => _mimeType is { Length: > 0 } ? new BlobHttpHeaders { ContentType = _mimeType } : null;

    private Dictionary<string, string>? CreateMetadata()
        => _journalFormatKey is { Length: > 0 } ? new Dictionary<string, string> { [FormatKeyMetadataKey] = _journalFormatKey } : null;

    private void ValidateFormatKeyMetadata(IDictionary<string, string>? metadata)
    {
        if (_journalFormatKey is not { Length: > 0 })
        {
            return;
        }

        if (metadata is null
            || !metadata.TryGetValue(FormatKeyMetadataKey, out var storedKey)
            || storedKey is not { Length: > 0 })
        {
            // Legacy blob written before format-key metadata was stamped. Allow it through;
            // recovery will surface a parse-time format mismatch if the encoding differs.
            return;
        }

        if (!string.Equals(storedKey, _journalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Blob \"{_client.BlobContainerName}/{_client.Name}\" was written with journal format key '{storedKey}', " +
                $"but the configured key is '{_journalFormatKey}'. Reconfigure the format or migrate the data.");
        }
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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Deleted orphan snapshot \"{Snapshot}\" of blob \"{ContainerName}/{BlobName}\"")]
    private static partial void LogOrphanSnapshotDeleted(ILogger logger, string containerName, string blobName, string snapshot);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete orphan snapshot \"{Snapshot}\" of blob \"{ContainerName}/{BlobName}\"")]
    private static partial void LogOrphanSnapshotDeleteFailed(ILogger logger, Exception exception, string containerName, string blobName, string snapshot);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to enumerate snapshots of blob \"{ContainerName}/{BlobName}\" while cleaning up orphans")]
    private static partial void LogOrphanSnapshotEnumerationFailed(ILogger logger, Exception exception, string containerName, string blobName);

    private sealed class ReadOnlySequenceStream(ReadOnlySequence<byte> sequence) : Stream
    {
        private readonly ReadOnlySequence<byte> _sequence = sequence;
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;

        public override bool CanSeek => !_disposed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _sequence.Length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0 || value > _sequence.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            if (buffer.IsEmpty)
            {
                return 0;
            }

            var remaining = _sequence.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            _sequence.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<int>(cancellationToken);
            }

            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _sequence.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            Position = position;
            return _position;
        }

        public override void SetLength(long value) => throw GetReadOnlyException();

        public override void Write(byte[] buffer, int offset, int count) => throw GetReadOnlyException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw GetReadOnlyException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReadOnlySequenceStream));
            }
        }

        private static NotSupportedException GetReadOnlyException() => new("This stream is read-only.");
    }
}
