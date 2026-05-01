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
    private readonly IAppendBlobClient _client;
    private readonly ILogger<AzureAppendBlobLogStorage> _logger;
    private readonly AppendBlobAppendBlockOptions _appendOptions;
    private bool _exists;
    private int _numBlocks;

    public bool IsCompactionRequested => _numBlocks > 10;

    public AzureAppendBlobLogStorage(AppendBlobClient client, ILogger<AzureAppendBlobLogStorage> logger)
        : this(new AppendBlobClientAdapter(client), logger)
    {
    }

    internal AzureAppendBlobLogStorage(IAppendBlobClient client, ILogger<AzureAppendBlobLogStorage> logger)
    {
        _client = client;
        _logger = logger;

        // For the first request, if we have not performed a read yet, we want to guard against clobbering an existing blob.
        _appendOptions = new AppendBlobAppendBlockOptions() { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } };
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        if (!_exists)
        {
            var response = await _client.CreateAsync(CreateOptions, cancellationToken);
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

    public async ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
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
            var mem = buffer.GetMemory();
            var bytesRead = await rawStream.ReadAsync(mem, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                LogRead(_logger, totalBytesRead, _client.BlobContainerName, _client.Name);
                return;
            }

            buffer.AdvanceWriter(bytesRead);
            totalBytesRead += bytesRead;
            consume(new ArcBufferReader(buffer));
        }
    }

    private async Task<Response<BlobDownloadStreamingResult>> CopyFromSnapshotAsync(ETag eTag, string snapshotDetail, CancellationToken cancellationToken)
    {
        // Read snapshot and append it to the blob.
        var snapshot = _client.WithSnapshot(snapshotDetail);
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
        using var stream = new ReadOnlySequenceStream(value);
        var result = await _client.AppendBlockAsync(stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        LogReplace(_logger, _client.BlobContainerName, _client.Name, stream.Length);

        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;

        // Delete the blob snapshot.
        await _client.WithSnapshot(blobSnapshot.Value.Snapshot).DeleteAsync(conditions: null, cancellationToken: cancellationToken).ConfigureAwait(false);
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

internal interface IAppendBlobClient
{
    string BlobContainerName { get; }
    string Name { get; }
    Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken);
    Task<Response<BlobAppendInfo>> AppendBlockAsync(Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken);
    Task DeleteAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken);
    Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(CancellationToken cancellationToken);
    Task<Response<BlobCopyInfo>> SyncCopyFromUriAsync(Uri source, BlobCopyFromUriOptions options, CancellationToken cancellationToken);
    Task<Response<BlobSnapshotInfo>> CreateSnapshotAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken);
    IAppendBlobClient WithSnapshot(string snapshot);
    Uri GenerateSasUri(BlobSasPermissions permissions, DateTimeOffset expiresOn);
}

internal sealed class AppendBlobClientAdapter(AppendBlobClient client) : IAppendBlobClient
{
    public string BlobContainerName => client.BlobContainerName;
    public string Name => client.Name;

    public async Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken)
        => await client.CreateAsync(options, cancellationToken).ConfigureAwait(false);

    public async Task<Response<BlobAppendInfo>> AppendBlockAsync(Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken)
        => await client.AppendBlockAsync(content, options, cancellationToken).ConfigureAwait(false);

    public async Task DeleteAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken)
        => await client.DeleteAsync(conditions: conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(CancellationToken cancellationToken)
        => await client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<Response<BlobCopyInfo>> SyncCopyFromUriAsync(Uri source, BlobCopyFromUriOptions options, CancellationToken cancellationToken)
        => await client.SyncCopyFromUriAsync(source, options, cancellationToken).ConfigureAwait(false);

    public async Task<Response<BlobSnapshotInfo>> CreateSnapshotAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken)
        => await client.CreateSnapshotAsync(conditions: conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

    public IAppendBlobClient WithSnapshot(string snapshot) => new AppendBlobClientAdapter(client.WithSnapshot(snapshot));

    public Uri GenerateSasUri(BlobSasPermissions permissions, DateTimeOffset expiresOn) => client.GenerateSasUri(permissions, expiresOn);
}
