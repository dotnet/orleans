using System.Buffers;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Orleans.Journaling;

internal sealed partial class AzureBlobJournalStorage : IJournalStorage
{
    internal const string FormatMetadataKey = "format";
    internal const string SnapshotMetadataKey = "snapshot";
    internal const string SnapshotETagMetadataKey = "snapshot-etag";
    internal const string SnapshotFormatMetadataKey = "snapshot-format";
    internal const string SnapshotLengthMetadataKey = "snapshot-length";

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
    private readonly Func<string> _snapshotNameFactory;
    private readonly Func<string, BlockBlobClient> _snapshotClientFactory;
    private readonly string? _mimeType;
    private readonly string? _journalFormatKey;
    private readonly ILogger<AzureBlobJournalStorage> _logger;
    private readonly AppendBlobAppendBlockOptions _appendOptions;
    private bool _exists;
    private int _numBlocks;

    public bool IsCompactionRequested => _numBlocks > 10;

    public AzureBlobJournalStorage(AppendBlobClient client, ILogger<AzureBlobJournalStorage> logger)
        : this(client, mimeType: null, logger)
    {
    }

    internal AzureBlobJournalStorage(AppendBlobClient client, string? mimeType, ILogger<AzureBlobJournalStorage> logger)
        : this(client, mimeType, logger, journalFormatKey: null)
    {
    }

    internal AzureBlobJournalStorage(
        AppendBlobClient client,
        string? mimeType,
        ILogger<AzureBlobJournalStorage> logger,
        string? journalFormatKey = null,
        Func<string>? snapshotNameFactory = null,
        Func<string, BlockBlobClient>? snapshotClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _snapshotNameFactory = snapshotNameFactory ?? CreateDefaultSnapshotNameFactory(client);
        _snapshotClientFactory = snapshotClientFactory ?? CreateDefaultSnapshotClientFactory(client);
        _mimeType = mimeType;
        _journalFormatKey = journalFormatKey;
        _logger = logger;

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

    private static void ThrowIfBatchTooLarge(long length, bool isReplace)
    {
        if (length <= MaxAppendBlockBytes)
        {
            return;
        }

        var operation = isReplace ? "compacted journal" : "journal batch";
        throw new InvalidOperationException(
            $"Azure Append Blob {operation} of {length:N0} bytes exceeds the per-block limit of {MaxAppendBlockBytes:N0} bytes (100 MiB). " +
            "Reduce the operation size or compact more aggressively.");
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
            consumer.Complete(metadata: null);
            return;
        }

        await using var appendStream = result.Value.Content;
        var metadata = result.Value.Details.Metadata;
        var fileMetadata = CreateFileMetadata(metadata);

        _numBlocks = result.Value.Details.BlobCommittedBlockCount;
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.Details.ETag;
        _exists = true;

        var snapshot = CreateSnapshotReference(metadata);
        if (snapshot is null)
        {
            var totalBytesRead = await consumer.ReadAsync(appendStream, fileMetadata, cancellationToken).ConfigureAwait(false);
            LogRead(_logger, totalBytesRead, _client.BlobContainerName, _client.Name);
            return;
        }

        var snapshotClient = _snapshotClientFactory(snapshot.Name);
        if (snapshotClient is null)
        {
            throw new InvalidOperationException("The configured Azure Blob journal snapshot client factory returned null.");
        }

        var snapshotResult = await snapshotClient.DownloadStreamingAsync(
            new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = snapshot.ETag }
            },
            cancellationToken).ConfigureAwait(false);
        await using var snapshotStream = snapshotResult.Value.Content;

        var readMetadata = ValidateSnapshotMetadata(snapshot, snapshotResult.Value.Details, metadata);
        await using var combinedStream = new ConcatenatedReadStream(snapshotStream, appendStream);
        var combinedBytesRead = await consumer.ReadAsync(combinedStream, readMetadata, cancellationToken).ConfigureAwait(false);
        LogRead(_logger, combinedBytesRead, _client.BlobContainerName, _client.Name);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var snapshotName = _snapshotNameFactory();
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            throw new InvalidOperationException("The configured Azure Blob journal snapshot name factory returned an empty blob name.");
        }

        var snapshotClient = _snapshotClientFactory(snapshotName);
        if (snapshotClient is null)
        {
            throw new InvalidOperationException("The configured Azure Blob journal snapshot client factory returned null.");
        }

        using var snapshotStream = new ReadOnlySequenceStream(value);
        var snapshotResult = await snapshotClient.UploadAsync(
            snapshotStream,
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = CreateHttpHeaders(),
                Metadata = CreateMetadata(),
            },
            cancellationToken).ConfigureAwait(false);

        // Open the append blob for writing, atomically replacing it with an empty tail marker.
        var createOptions = new AppendBlobCreateOptions()
        {
            Conditions = new AppendBlobRequestConditions
            {
                IfMatch = _appendOptions.Conditions.IfMatch,
                IfNoneMatch = _appendOptions.Conditions.IfNoneMatch,
            },
            Metadata = CreateSnapshotMarkerMetadata(snapshotName, snapshotResult.Value.ETag, snapshotStream.Length),
            HttpHeaders = CreateHttpHeaders(),
        };
        var createResult = await _client.CreateAsync(createOptions, cancellationToken).ConfigureAwait(false);
        _appendOptions.Conditions.IfMatch = createResult.Value.ETag;
        _appendOptions.Conditions.IfNoneMatch = default;
        _exists = true;
        _numBlocks = 0;
        LogReplace(_logger, _client.BlobContainerName, _client.Name, snapshotStream.Length);
    }

    private AppendBlobCreateOptions CreateOptions(AppendBlobRequestConditions conditions) => new()
    {
        Conditions = conditions,
        HttpHeaders = CreateHttpHeaders(),
        Metadata = CreateMetadata(),
    };

    private BlobHttpHeaders? CreateHttpHeaders() => CreateHttpHeaders(_mimeType);

    private static BlobHttpHeaders? CreateHttpHeaders(string? contentType)
        => contentType is { Length: > 0 } ? new BlobHttpHeaders { ContentType = contentType } : null;

    private static Func<string> CreateDefaultSnapshotNameFactory(AppendBlobClient client)
        => () => $"{client.Name}.snapshots/{Guid.NewGuid():N}";

    private static Func<string, BlockBlobClient> CreateDefaultSnapshotClientFactory(AppendBlobClient client)
        => snapshotName => client.GetParentBlobContainerClient().GetBlockBlobClient(snapshotName);

    private Dictionary<string, string> CreateMetadataDictionary()
    {
        var metadata = new Dictionary<string, string>();
        if (_journalFormatKey is { Length: > 0 })
        {
            metadata[FormatMetadataKey] = _journalFormatKey;
        }

        return metadata;
    }

    private Dictionary<string, string>? CreateMetadata()
    {
        var metadata = CreateMetadataDictionary();
        return metadata.Count > 0 ? metadata : null;
    }

    private Dictionary<string, string> CreateSnapshotMarkerMetadata(string snapshotName, ETag snapshotETag, long snapshotLength)
    {
        var metadata = CreateMetadataDictionary();
        metadata[SnapshotMetadataKey] = snapshotName;
        metadata[SnapshotETagMetadataKey] = snapshotETag.ToString();
        metadata[SnapshotLengthMetadataKey] = snapshotLength.ToString(CultureInfo.InvariantCulture);
        if (_journalFormatKey is { Length: > 0 })
        {
            metadata[SnapshotFormatMetadataKey] = _journalFormatKey;
        }

        return metadata;
    }

    private static string? GetFormatKeyMetadata(IDictionary<string, string>? metadata)
        => metadata is not null
            && metadata.TryGetValue(FormatMetadataKey, out var storedKey)
            && storedKey is { Length: > 0 }
                ? storedKey
                : null;

    private static IJournalFileMetadata CreateFileMetadata(IDictionary<string, string>? metadata)
        => GetFormatKeyMetadata(metadata) is { } format
            ? new JournalFileMetadata(format)
            : JournalFileMetadata.Empty;

    private static SnapshotReference? CreateSnapshotReference(IDictionary<string, string>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(SnapshotMetadataKey, out var snapshotName) || snapshotName is not { Length: > 0 })
        {
            return null;
        }

        if (!metadata.TryGetValue(SnapshotETagMetadataKey, out var snapshotETag) || snapshotETag is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references snapshot blob '{snapshotName}' but does not include '{SnapshotETagMetadataKey}' metadata.");
        }

        if (!metadata.TryGetValue(SnapshotLengthMetadataKey, out var snapshotLengthValue)
            || !long.TryParse(snapshotLengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var snapshotLength)
            || snapshotLength < 0)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references snapshot blob '{snapshotName}' but does not include valid '{SnapshotLengthMetadataKey}' metadata.");
        }

        var snapshotFormat = metadata.TryGetValue(SnapshotFormatMetadataKey, out var storedSnapshotFormat)
            && storedSnapshotFormat is { Length: > 0 }
                ? storedSnapshotFormat
                : null;

        return new SnapshotReference(snapshotName, new ETag(snapshotETag), snapshotFormat, snapshotLength);
    }

    private static IJournalFileMetadata ValidateSnapshotMetadata(
        SnapshotReference snapshot,
        BlobDownloadDetails snapshotDetails,
        IDictionary<string, string>? markerMetadata)
    {
        if (snapshotDetails.ContentLength != snapshot.Length)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal snapshot '{snapshot.Name}' length is {snapshotDetails.ContentLength}, but marker metadata expected {snapshot.Length}.");
        }

        var markerFormat = GetFormatKeyMetadata(markerMetadata);
        var snapshotBlobFormat = GetFormatKeyMetadata(snapshotDetails.Metadata);
        if (snapshot.Format is { } markerSnapshotFormat
            && snapshotBlobFormat is { }
            && !string.Equals(markerSnapshotFormat, snapshotBlobFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal snapshot '{snapshot.Name}' format metadata is '{snapshotBlobFormat}', but marker metadata expected '{markerSnapshotFormat}'.");
        }

        var effectiveSnapshotFormat = snapshot.Format ?? snapshotBlobFormat;
        if (markerFormat is { } tailFormat
            && effectiveSnapshotFormat is { }
            && !string.Equals(tailFormat, effectiveSnapshotFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal append tail format metadata is '{tailFormat}', but snapshot '{snapshot.Name}' uses format '{effectiveSnapshotFormat}'.");
        }

        if (markerFormat is { } && effectiveSnapshotFormat is null)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal snapshot '{snapshot.Name}' does not include format metadata.");
        }

        return effectiveSnapshotFormat is { } format
            ? new JournalFileMetadata(format)
            : JournalFileMetadata.Empty;
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
        Message = "Replaced blob \"{ContainerName}/{BlobName}\" with snapshot containing {Length} bytes")]
    private static partial void LogReplace(ILogger logger, string containerName, string blobName, long length);

    private sealed record SnapshotReference(string Name, ETag ETag, string? Format, long Length);

    private sealed class ConcatenatedReadStream(Stream first, Stream second) : Stream
    {
        // The caller owns and disposes the underlying streams.
        private Stream? _current = first;
        private Stream? _next = second;
        private bool _disposed;

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw GetSeekNotSupportedException();

        public override long Position
        {
            get => throw GetSeekNotSupportedException();
            set => throw GetSeekNotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            while (_current is not null)
            {
                var bytesRead = _current.Read(buffer);
                if (bytesRead != 0 || _next is null)
                {
                    return bytesRead;
                }

                _current = _next;
                _next = null;
            }

            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            while (_current is not null)
            {
                var bytesRead = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead != 0 || _next is null)
                {
                    return bytesRead;
                }

                _current = _next;
                _next = null;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw GetSeekNotSupportedException();

        public override void SetLength(long value) => throw GetReadOnlyException();

        public override void Write(byte[] buffer, int offset, int count) => throw GetReadOnlyException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw GetReadOnlyException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _current = null;
                _next = null;
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _current = null;
                _next = null;
                _disposed = true;
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConcatenatedReadStream));
            }
        }

        private static NotSupportedException GetSeekNotSupportedException() => new("This stream does not support seeking.");

        private static NotSupportedException GetReadOnlyException() => new("This stream is read-only.");
    }

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
