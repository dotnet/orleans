using System.Buffers;
using Azure;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed partial class AzureAppendBlobLogStorage : ILogStorage
{
    private static readonly AppendBlobCreateOptions CreateOptions = new() { Conditions = new() { IfNoneMatch = ETag.All } };
    private readonly AppendBlobClient _client;
    private readonly ILogger<AzureAppendBlobLogStorage> _logger;
    private readonly string _logFormatKey;
    private readonly AppendBlobAppendBlockOptions _appendOptions;
    private bool _exists;
    private int _numBlocks;

    public bool IsCompactionRequested => _numBlocks > 10;

    public AzureAppendBlobLogStorage(AppendBlobClient client, ILogger<AzureAppendBlobLogStorage> logger, string logFormatKey)
    {
        _client = client;
        _logger = logger;
        ArgumentException.ThrowIfNullOrWhiteSpace(logFormatKey);
        _logFormatKey = logFormatKey;

        // For the first request, if we have not performed a read yet, we want to guard against clobbering an existing blob.
        _appendOptions = new AppendBlobAppendBlockOptions() { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } };
    }

    public string LogFormatKey => _logFormatKey;

    public async ValueTask AppendAsync(ArcBuffer value, CancellationToken cancellationToken)
    {
        if (!_exists)
        {
            var response = await _client.CreateAsync(CreateOptions, cancellationToken);
            _appendOptions.Conditions.IfNoneMatch = default;
            _appendOptions.Conditions.IfMatch = response.Value.ETag;
            _exists = true;
        }

        using var stream = new BorrowedArcBufferReadOnlyStream(value);
        var result = await _client.AppendBlockAsync(stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogAppend(_logger, stream.Length, _client.BlobContainerName, _client.Name);

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

    public async ValueTask ReadAsync(ILogDataSink consumer, CancellationToken cancellationToken)
    {
        Response<BlobDownloadStreamingResult> result;
        try
        {
            result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            _exists = false;
            return;
        }

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

        await using var rawStream = result.Value.Content;
        long totalBytesRead = 0;
        while (true)
        {
            using var buffer = new ArcBufferWriter();
            var mem = buffer.GetMemory();
            var bytesRead = await rawStream.ReadAsync(mem, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                LogRead(_logger, totalBytesRead, _client.BlobContainerName, _client.Name);
                return;
            }

            buffer.AdvanceWriter(bytesRead);
            totalBytesRead += bytesRead;
            using var data = buffer.ConsumeSlice(buffer.Length);
            consumer.OnLogData(data);
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

    public async ValueTask ReplaceAsync(ArcBuffer value, CancellationToken cancellationToken)
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
        using var stream = new BorrowedArcBufferReadOnlyStream(value);
        var result = await _client.AppendBlockAsync(stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogReplace(_logger, _client.BlobContainerName, _client.Name, stream.Length);

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

    private sealed class BorrowedArcBufferReadOnlyStream(ArcBuffer buffer) : Stream
    {
        private readonly ReadOnlySequence<byte> _sequence = buffer.AsReadOnlySequence();
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
                throw new ObjectDisposedException(nameof(BorrowedArcBufferReadOnlyStream));
            }
        }

        private static NotSupportedException GetReadOnlyException() => new("This stream is read-only.");
    }
}
