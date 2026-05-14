using System.Buffers;
using Azure;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Orleans.Journaling;

internal sealed partial class AzureBlobJournalStorage : IJournalStorage
{
    internal const string FormatMetadataKey = "format";

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
        string? journalFormatKey = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
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

        var metadata = CreateFileMetadata(result.Value.Details.Metadata);

        _numBlocks = result.Value.Details.BlobCommittedBlockCount;
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.Details.ETag;
        _exists = true;

        await using (var rawStream = result.Value.Content)
        {
            var totalBytesRead = await consumer.ReadAsync(rawStream, metadata, cancellationToken).ConfigureAwait(false);
            LogRead(_logger, totalBytesRead, _client.BlobContainerName, _client.Name);
        }
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        ThrowIfBatchTooLarge(value.Length, isReplace: true);

        // Open the blob for writing, overwriting existing contents.
        var createOptions = new AppendBlobCreateOptions()
        {
            Conditions = new AppendBlobRequestConditions
            {
                IfMatch = _appendOptions.Conditions.IfMatch,
                IfNoneMatch = _appendOptions.Conditions.IfNoneMatch,
            },
            Metadata = CreateMetadata(),
            HttpHeaders = CreateHttpHeaders(),
        };
        var createResult = await _client.CreateAsync(createOptions, cancellationToken).ConfigureAwait(false);
        _appendOptions.Conditions.IfMatch = createResult.Value.ETag;
        _appendOptions.Conditions.IfNoneMatch = default;

        // Write the compacted journal state.
        using var stream = new ReadOnlySequenceStream(value);
        var result = await _client.AppendBlockAsync(stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogReplace(_logger, _client.BlobContainerName, _client.Name, stream.Length);

        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;
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

    private Dictionary<string, string>? CreateMetadata()
        => _journalFormatKey is { Length: > 0 } ? new Dictionary<string, string> { [FormatMetadataKey] = _journalFormatKey } : null;

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
