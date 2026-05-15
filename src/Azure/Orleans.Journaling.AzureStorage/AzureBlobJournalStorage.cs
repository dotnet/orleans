using System.Buffers;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed partial class AzureBlobJournalStorage : IJournalStorage
{
    internal const string FormatMetadataKey = "format";
    internal const string CheckpointMetadataKey = "checkpoint";
    internal const string CheckpointOffsetMetadataKey = "checkpoint_offset";

    // Azure Append Blob hard limits documented at:
    // https://learn.microsoft.com/azure/storage/blobs/scalability-targets#scale-targets-for-blob-storage
    // The append-block size cap is 100 MiB for service version >= 2022-11-02.
    internal const long MaxAppendBlockBytes = 100L * 1024 * 1024;

    // An append blob can hold up to 50,000 committed blocks. The compaction request
    // (IsCompactionRequested) trips earlier, above 49,000 blocks, so this guard exists
    // only to fail clearly before Azure rejects the append.
    private const int MaxBlocksPerBlob = 50_000;
    private const int HeadroomBlockCount = 100;
    private const int RequestCompactionBlockCount = 49_000;

    private readonly SharedConfiguration _shared;
    private readonly IGrainContext _grainContext;
    private readonly AppendBlobClient _walClient;
    private int _numBlocks;
    private long _walLength;
    private ETag _walETag;
    private string? _checkpointName;

    private bool WalExists => _walETag != default;

    public bool IsCompactionRequested => _numBlocks > RequestCompactionBlockCount;

    internal AzureBlobJournalStorage(
        SharedConfiguration shared,
        IGrainContext grainContext)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(grainContext);

        _shared = shared;
        _grainContext = grainContext;
        _walClient = GetWalClient();
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        ThrowIfBatchTooLarge(value.Length);
        await EnsureWalAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfCompactionRequired();

        using var stream = new ReadOnlySequenceStream(value);
        try
        {
            var result = await _walClient.AppendBlockAsync(
                stream,
                new AppendBlobAppendBlockOptions
                {
                    Conditions = new AppendBlobRequestConditions
                    {
                        IfMatch = _walETag,
                    }
                },
                cancellationToken).ConfigureAwait(false);

            LogAppend(_shared.Logger, stream.Length, _walClient.BlobContainerName, _walClient.Name);
            SetWal(result.Value.ETag, result.Value.BlobCommittedBlockCount, _walLength + stream.Length, _checkpointName);
        }
        catch (RequestFailedException exception) when (IsBlobSealed(exception))
        {
            throw new InvalidOperationException("Azure Blob journal WAL is sealed; recovery is required before appending.", exception);
        }
        catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
        {
            throw new InvalidOperationException("Azure Blob journal WAL changed while appending; recovery is required.", exception);
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        if (!WalExists && !await TryLoadWalStateAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var checkpointName = _checkpointName;
        try
        {
            await _walClient.DeleteIfExistsAsync(
                DeleteSnapshotsOption.None,
                new BlobRequestConditions { IfMatch = _walETag },
                cancellationToken).ConfigureAwait(false);
            SetWal(eTag: default, blockCount: 0, length: 0, checkpointName: null);
        }
        catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
        {
            throw new InvalidOperationException("Azure Blob journal WAL changed while deleting the journal; recovery is required.", exception);
        }

        if (checkpointName is not null)
        {
            await DeleteCheckpointIfExistsAsync(checkpointName, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        Response<BlobDownloadStreamingResult> walResult;
        try
        {
            walResult = await _walClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            SetWal(eTag: default, blockCount: 0, length: 0, checkpointName: null);
            consumer.Complete(metadata: null);
            return;
        }

        var walDetails = walResult.Value.Details;
        var manifest = CreateWalManifest(walDetails.Metadata);
        SetWal(walDetails.ETag, walDetails.BlobCommittedBlockCount, walDetails.ContentLength, manifest.Checkpoint?.Name);

        await using var walStream = walResult.Value.Content;
        var expectedFormat = manifest.Metadata.Format;
        if (manifest.Checkpoint is { } checkpoint)
        {
            var checkpointClient = GetCheckpointClient(checkpoint.Name);
            var checkpointResult = await checkpointClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using var checkpointStream = checkpointResult.Value.Content;

            var checkpointMetadata = ValidateCheckpointMetadata(checkpoint, checkpointResult.Value.Details, expectedFormat);
            var totalCheckpointBytes = await ReadStreamAsync(
                consumer,
                checkpointStream,
                checkpointMetadata,
                complete: false,
                cancellationToken).ConfigureAwait(false);
            LogRead(_shared.Logger, totalCheckpointBytes, checkpointClient.BlobContainerName, checkpointClient.Name);
            expectedFormat = checkpointMetadata.Format;
        }

        if (manifest.Checkpoint is { WalOffset: > 0 } checkpointOffset)
        {
            if (checkpointOffset.WalOffset > walDetails.ContentLength)
            {
                throw new InvalidOperationException(
                    $"Azure Blob journal checkpoint offset {checkpointOffset.WalOffset:N0} exceeds WAL length {walDetails.ContentLength:N0}.");
            }

            await SkipStreamAsync(walStream, checkpointOffset.WalOffset, cancellationToken).ConfigureAwait(false);
        }

        var walMetadata = manifest.Metadata.Format is { Length: > 0 }
            ? manifest.Metadata
            : expectedFormat is { Length: > 0 }
                ? new JournalFileMetadata(expectedFormat)
                : JournalFileMetadata.Empty;
        var totalWalBytes = await ReadStreamAsync(
            consumer,
            walStream,
            walMetadata,
            complete: false,
            cancellationToken).ConfigureAwait(false);
        LogRead(_shared.Logger, totalWalBytes, _walClient.BlobContainerName, _walClient.Name);
        consumer.Complete(walMetadata);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        await EnsureWalAsync(cancellationToken).ConfigureAwait(false);

        var walETag = _walETag;
        var previousCheckpointName = _checkpointName;
        using var checkpointStream = new ReadOnlySequenceStream(value);
        while (true)
        {
            var checkpointName = GetCheckpointName(Guid.NewGuid().ToString("N"));
            var checkpointClient = GetCheckpointClient(checkpointName);
            try
            {
                checkpointStream.Position = 0;
                await checkpointClient.UploadAsync(
                    checkpointStream,
                    new BlobUploadOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = CreateHttpHeaders(_shared.MimeType),
                        Metadata = CreateCheckpointBlobMetadata(),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (IsBlobAlreadyExists(exception))
            {
                // Snapshot ids are random, so this should be vanishingly rare. Retry with a new id.
                continue;
            }

            try
            {
                var result = await CreateWalAsync(
                    checkpointName,
                    new AppendBlobRequestConditions { IfMatch = walETag },
                    cancellationToken).ConfigureAwait(false);
                SetWal(result.Value.ETag, blockCount: 0, length: 0, checkpointName);
            }
            catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
            {
                throw new InvalidOperationException("Azure Blob journal WAL changed while publishing a checkpoint; recovery is required.", exception);
            }

            if (previousCheckpointName is not null && !string.Equals(previousCheckpointName, checkpointName, StringComparison.Ordinal))
            {
                await DeleteCheckpointIfExistsAsync(previousCheckpointName, cancellationToken).ConfigureAwait(false);
            }

            LogReplace(_shared.Logger, checkpointClient.BlobContainerName, checkpointClient.Name, checkpointStream.Length);
            return;
        }
    }

    private static void ThrowIfBatchTooLarge(long length)
    {
        if (length <= MaxAppendBlockBytes)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Azure Append Blob journal batch of {length:N0} bytes exceeds the per-block limit of {MaxAppendBlockBytes:N0} bytes (100 MiB). " +
            "Reduce the operation size or compact more aggressively.");
    }

    private void ThrowIfCompactionRequired()
    {
        if (_numBlocks < MaxBlocksPerBlob - HeadroomBlockCount)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Azure Blob journal WAL has {_numBlocks:N0} committed append blocks and must be compacted before more appends. " +
            $"Azure Append Blob supports at most {MaxBlocksPerBlob:N0} committed blocks.");
    }

    private async ValueTask EnsureWalAsync(CancellationToken cancellationToken)
    {
        while (!WalExists)
        {
            try
            {
                var response = await CreateWalAsync(
                    checkpointName: null,
                    new AppendBlobRequestConditions { IfNoneMatch = ETag.All },
                    cancellationToken).ConfigureAwait(false);
                SetWal(response.Value.ETag, blockCount: 0, length: 0, checkpointName: null);
                return;
            }
            catch (RequestFailedException exception) when (IsBlobAlreadyExists(exception))
            {
                await ReadAsync(DiscardingJournalStorageConsumer.Instance, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<bool> TryLoadWalStateAsync(CancellationToken cancellationToken)
    {
        Response<BlobDownloadStreamingResult> walResult;
        try
        {
            walResult = await _walClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            SetWal(eTag: default, blockCount: 0, length: 0, checkpointName: null);
            return false;
        }

        await using var content = walResult.Value.Content;
        var walDetails = walResult.Value.Details;
        var manifest = CreateWalManifest(walDetails.Metadata);
        SetWal(walDetails.ETag, walDetails.BlobCommittedBlockCount, walDetails.ContentLength, manifest.Checkpoint?.Name);
        return true;
    }

    private void SetWal(ETag eTag, int blockCount, long length, string? checkpointName)
    {
        _walETag = eTag;
        _numBlocks = blockCount;
        _walLength = length;
        _checkpointName = checkpointName;
    }

    private AppendBlobClient GetWalClient()
    {
        var client = _shared.BlobClientProvider.GetWalClient(_grainContext.GrainId);
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal WAL client provider returned null.");
    }

    private async ValueTask<Response<BlobContentInfo>> CreateWalAsync(
        string? checkpointName,
        AppendBlobRequestConditions conditions,
        CancellationToken cancellationToken)
        => await _walClient.CreateAsync(
            new AppendBlobCreateOptions
            {
                Conditions = conditions,
                HttpHeaders = CreateHttpHeaders(_shared.MimeType),
                Metadata = CreateWalMetadata(checkpointName, checkpointOffset: 0),
            },
            cancellationToken).ConfigureAwait(false);

    private static BlobHttpHeaders? CreateHttpHeaders(string? contentType)
        => contentType is { Length: > 0 } ? new BlobHttpHeaders { ContentType = contentType } : null;

    private string GetCheckpointName(string snapshotId)
    {
        var checkpointName = _shared.BlobClientProvider.GetCheckpointName(_grainContext.GrainId, snapshotId);
        if (string.IsNullOrWhiteSpace(checkpointName))
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned an empty blob name.");
        }

        return checkpointName;
    }

    private BlockBlobClient GetCheckpointClient(string checkpointName)
    {
        var client = _shared.BlobClientProvider.GetCheckpointClient(_grainContext.GrainId, checkpointName);
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned null.");
    }

    private async ValueTask DeleteCheckpointIfExistsAsync(string checkpointName, CancellationToken cancellationToken)
    {
        var checkpointClient = GetCheckpointClient(checkpointName);
        try
        {
            await checkpointClient.DeleteIfExistsAsync(DeleteSnapshotsOption.None, conditions: null, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception)
        {
            LogCheckpointCleanupFailure(_shared.Logger, checkpointClient.BlobContainerName, checkpointClient.Name, exception);
        }
    }

    private Dictionary<string, string> CreateMetadataDictionary()
    {
        var metadata = new Dictionary<string, string>();
        if (_shared.JournalFormatKey is { Length: > 0 })
        {
            metadata[FormatMetadataKey] = _shared.JournalFormatKey;
        }

        return metadata;
    }

    private Dictionary<string, string> CreateCheckpointBlobMetadata() => CreateMetadataDictionary();

    private Dictionary<string, string> CreateWalMetadata(string? checkpointName, long checkpointOffset)
    {
        var metadata = CreateMetadataDictionary();
        if (checkpointName is not null)
        {
            metadata[CheckpointMetadataKey] = checkpointName;
            metadata[CheckpointOffsetMetadataKey] = checkpointOffset.ToString(CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static string? GetFormatKeyMetadata(IDictionary<string, string>? metadata)
        => metadata is not null
            && metadata.TryGetValue(FormatMetadataKey, out var storedKey)
            && storedKey is { Length: > 0 }
                ? storedKey
                : null;

    private static WalManifest CreateWalManifest(IDictionary<string, string>? metadata)
    {
        var fileMetadata = GetFormatKeyMetadata(metadata) is { } format
            ? new JournalFileMetadata(format)
            : JournalFileMetadata.Empty;
        if (metadata is null || !metadata.TryGetValue(CheckpointMetadataKey, out var checkpointName) || checkpointName is not { Length: > 0 })
        {
            return new WalManifest(fileMetadata, Checkpoint: null);
        }

        var checkpointOffset = 0L;
        if (metadata.TryGetValue(CheckpointOffsetMetadataKey, out var checkpointOffsetValue)
            && checkpointOffsetValue is { Length: > 0 }
            && (!long.TryParse(checkpointOffsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out checkpointOffset) || checkpointOffset < 0))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint offset metadata is invalid: '{checkpointOffsetValue}'.");
        }

        return new WalManifest(fileMetadata, new CheckpointReference(checkpointName, checkpointOffset));
    }

    private static IJournalFileMetadata ValidateCheckpointMetadata(CheckpointReference checkpoint, BlobDownloadDetails checkpointDetails, string? expectedFormat)
    {
        var checkpointBlobFormat = GetFormatKeyMetadata(checkpointDetails.Metadata);
        if (expectedFormat is { Length: > 0 })
        {
            if (checkpointBlobFormat is null)
            {
                throw new InvalidOperationException(
                    $"Azure Blob journal checkpoint '{checkpoint.Name}' does not include format metadata.");
            }

            if (!string.Equals(expectedFormat, checkpointBlobFormat, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Azure Blob journal checkpoint '{checkpoint.Name}' format metadata is '{checkpointBlobFormat}', but recovery expected '{expectedFormat}'.");
            }
        }

        return checkpointBlobFormat is { } format
            ? new JournalFileMetadata(format)
            : JournalFileMetadata.Empty;
    }

    private static bool IsBlobSealed(RequestFailedException exception)
        => exception.Status == 409
            && (string.Equals(exception.ErrorCode, "BlobIsSealed", StringComparison.Ordinal)
                || exception.Message.Contains("sealed", StringComparison.OrdinalIgnoreCase));

    private static bool IsBlobAlreadyExists(RequestFailedException exception)
        => exception.Status == 409
            && (string.Equals(exception.ErrorCode, "BlobAlreadyExists", StringComparison.Ordinal)
                || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));

    private static bool IsWalMutationConflict(RequestFailedException exception)
        => exception.Status is 404 or 412
            || exception.Status == 409 && string.Equals(exception.ErrorCode, "ConditionNotMet", StringComparison.Ordinal);

    private static async ValueTask<long> ReadStreamAsync(
        IJournalStorageConsumer consumer,
        Stream input,
        IJournalFileMetadata? metadata,
        bool complete,
        CancellationToken cancellationToken)
    {
        metadata ??= JournalFileMetadata.Empty;
        using var buffer = new ArcBufferWriter();
        long totalBytesRead = 0;
        while (true)
        {
            var memory = buffer.GetMemory();
            var bytesRead = await input.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                if (complete)
                {
                    ReadBuffer(consumer, buffer, metadata, isCompleted: true);
                }

                if (buffer.Length > 0)
                {
                    throw new InvalidOperationException("The journal storage consumer did not read all supplied journal data.");
                }

                return totalBytesRead;
            }

            buffer.AdvanceWriter(bytesRead);
            totalBytesRead += bytesRead;
            ReadBuffer(consumer, buffer, metadata, isCompleted: false);
        }
    }

    private static async ValueTask SkipStreamAsync(Stream input, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return;
        }

        if (input.CanSeek)
        {
            input.Seek(length, SeekOrigin.Current);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(81920, length));
        try
        {
            while (length > 0)
            {
                var bytesRead = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)),
                    cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new InvalidOperationException("Azure Blob journal WAL ended before the checkpoint offset was reached.");
                }

                length -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadBuffer(IJournalStorageConsumer consumer, ArcBufferWriter buffer, IJournalFileMetadata metadata, bool isCompleted)
    {
        var reader = new JournalBufferReader(buffer.Reader, isCompleted);
        consumer.Read(reader, metadata);
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
        Message = "Wrote checkpoint blob \"{ContainerName}/{BlobName}\" containing {Length} bytes")]
    private static partial void LogReplace(ILogger logger, string containerName, string blobName, long length);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete obsolete Azure Blob journal checkpoint \"{ContainerName}/{BlobName}\"")]
    private static partial void LogCheckpointCleanupFailure(ILogger logger, string containerName, string blobName, Exception exception);

    private sealed record WalManifest(IJournalFileMetadata Metadata, CheckpointReference? Checkpoint);

    private sealed record CheckpointReference(string Name, long WalOffset);

    internal sealed class SharedConfiguration
    {
        public SharedConfiguration(
            ILogger<AzureBlobJournalStorage> logger,
            BlobClientProvider blobClientProvider,
            string? mimeType = null,
            string? journalFormatKey = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(blobClientProvider);

            Logger = logger;
            MimeType = mimeType;
            JournalFormatKey = journalFormatKey;
            BlobClientProvider = blobClientProvider;
        }

        public ILogger<AzureBlobJournalStorage> Logger { get; }

        public string? MimeType { get; }

        public string? JournalFormatKey { get; }

        public BlobClientProvider BlobClientProvider { get; }
    }

    internal abstract class BlobClientProvider
    {
        public abstract AppendBlobClient GetWalClient(GrainId grainId);

        public abstract string GetCheckpointName(GrainId grainId, string snapshotId);

        public abstract BlockBlobClient GetCheckpointClient(GrainId grainId, string checkpointName);
    }

    internal sealed class OptionsBlobClientProvider(
        IBlobContainerFactory containerFactory,
        AzureBlobJournalStorageOptions options) : BlobClientProvider
    {
        public override AppendBlobClient GetWalClient(GrainId grainId)
            => containerFactory.GetBlobContainerClient(grainId).GetAppendBlobClient(
                options.GetWalBlobNameForJournal(grainId, options.GetBlobNameForJournal(grainId)));

        public override string GetCheckpointName(GrainId grainId, string snapshotId)
            => options.GetCheckpointBlobNameForJournal(grainId, options.GetBlobNameForJournal(grainId), snapshotId);

        public override BlockBlobClient GetCheckpointClient(GrainId grainId, string checkpointName)
            => containerFactory.GetBlobContainerClient(grainId).GetBlockBlobClient(checkpointName);
    }

    private sealed class DiscardingJournalStorageConsumer : IJournalStorageConsumer
    {
        public static DiscardingJournalStorageConsumer Instance { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata) => buffer.Skip(buffer.Length);
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
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _sequence.Length;
            }
        }

        public override long Position
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _position;
            }

            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value < 0 || value > _sequence.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty || _position >= _sequence.Length)
            {
                return 0;
            }

            var length = (int)Math.Min(buffer.Length, _sequence.Length - _position);
            _sequence.Slice(_position, length).CopyTo(buffer);
            _position += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _sequence.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            Position = newPosition;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException("This stream is read-only.");

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("This stream is read-only.");

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("This stream is read-only.");

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
