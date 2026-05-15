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
    internal const string CheckpointETagMetadataKey = "checkpoint-etag";
    internal const string CheckpointFormatMetadataKey = "checkpoint-format";
    internal const string CheckpointIdMetadataKey = "checkpoint-id";
    internal const string CheckpointLengthMetadataKey = "checkpoint-length";
    internal const string CheckpointReplaySegmentMetadataKey = "checkpoint-replay-segment";
    internal const string CheckpointReplayOffsetMetadataKey = "checkpoint-replay-offset";

    // Azure Append Blob hard limits documented at:
    // https://learn.microsoft.com/azure/storage/blobs/scalability-targets#scale-targets-for-blob-storage
    // The append-block size cap is 100 MiB for service version >= 2022-11-02.
    internal const long MaxAppendBlockBytes = 100L * 1024 * 1024;

    // An append blob can hold up to 50,000 committed blocks. The compaction request
    // (IsCompactionRequested) trips far earlier (at 10 blocks), so this guard exists only
    // to roll before hitting the hard Azure limit if a consumer ignores compaction requests.
    internal const int MaxAppendBlobBlocks = 50_000;
    private const int AppendBlobBlockCeilingHeadroom = 100;

    private readonly AppendBlobClient _rootLogClient;
    private readonly Func<uint, string> _walNameFactory;
    private readonly Func<uint, AppendBlobClient> _walClientFactory;
    private readonly Func<uint, string> _checkpointNameFactory;
    private readonly Func<string, BlockBlobClient> _checkpointClientFactory;
    private readonly string? _mimeType;
    private readonly string? _journalFormatKey;
    private readonly ILogger<AzureBlobJournalStorage> _logger;
    private AppendBlobClient _currentLogClient;
    private uint _currentSegmentId;
    private uint _nextCheckpointId;
    private bool _currentSegmentExists;
    private long _appendPosition;
    private int _numBlocks;
    private ETag _currentSegmentETag;
    private ETag _rootLogETag;

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
        Func<uint, string>? walNameFactory = null,
        Func<uint, AppendBlobClient>? walClientFactory = null,
        Func<uint, string>? checkpointNameFactory = null,
        Func<string, BlockBlobClient>? checkpointClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _rootLogClient = client;
        _currentLogClient = client;
        _walNameFactory = walNameFactory ?? CreateDefaultWalNameFactory(client);
        _walClientFactory = walClientFactory ?? CreateDefaultWalClientFactory(client);
        _checkpointNameFactory = checkpointNameFactory ?? CreateDefaultCheckpointNameFactory(client);
        _checkpointClientFactory = checkpointClientFactory ?? CreateDefaultCheckpointClientFactory(client);
        _mimeType = mimeType;
        _journalFormatKey = journalFormatKey;
        _logger = logger;
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        ThrowIfBatchTooLarge(value.Length);
        if (_currentSegmentExists && _numBlocks >= MaxAppendBlobBlocks - AppendBlobBlockCeilingHeadroom)
        {
            await RollToNextSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            await EnsureCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);

            using var stream = new ReadOnlySequenceStream(value);
            try
            {
                var result = await _currentLogClient.AppendBlockAsync(
                    stream,
                    new AppendBlobAppendBlockOptions
                    {
                        Conditions = new AppendBlobRequestConditions
                        {
                            IfAppendPositionEqual = _appendPosition,
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                LogAppend(_logger, stream.Length, _currentLogClient.BlobContainerName, _currentLogClient.Name);
                _appendPosition += stream.Length;
                _currentSegmentETag = result.Value.ETag;
                _numBlocks = result.Value.BlobCommittedBlockCount;
                if (_currentSegmentId == 0)
                {
                    _rootLogETag = result.Value.ETag;
                }

                return;
            }
            catch (RequestFailedException exception) when (IsBlobSealed(exception))
            {
                await RollToNextSegmentAsync(cancellationToken).ConfigureAwait(false);
            }
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

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var conditions = new BlobRequestConditions { IfMatch = _rootLogETag };
        await _rootLogClient.DeleteAsync(conditions: conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        SetCurrentSegment(segmentId: 0, exists: false, eTag: default, appendPosition: 0, blockCount: 0);
        _rootLogETag = default;
        _nextCheckpointId = 0;
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        Response<BlobDownloadStreamingResult> rootResult;
        try
        {
            rootResult = await _rootLogClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            SetCurrentSegment(segmentId: 0, exists: false, eTag: default, appendPosition: 0, blockCount: 0);
            _rootLogETag = default;
            _nextCheckpointId = 0;
            consumer.Complete(metadata: null);
            return;
        }

        await using var rootStream = rootResult.Value.Content;
        _rootLogETag = rootResult.Value.Details.ETag;
        var checkpoint = CreateCheckpointReference(rootResult.Value.Details.Metadata);
        if (checkpoint is null)
        {
            await ReadWalSegmentsAsync(
                consumer,
                startSegmentId: 0,
                startOffset: 0,
                firstSegmentResult: rootResult,
                firstSegmentStream: rootStream,
                expectedFormat: GetFormatKeyMetadata(rootResult.Value.Details.Metadata),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var checkpointClient = _checkpointClientFactory(checkpoint.Name);
        if (checkpointClient is null)
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint client factory returned null.");
        }

        var checkpointResult = await checkpointClient.DownloadStreamingAsync(
            new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = checkpoint.ETag }
            },
            cancellationToken).ConfigureAwait(false);
        await using var checkpointStream = checkpointResult.Value.Content;

        var checkpointMetadata = ValidateCheckpointMetadata(checkpoint, checkpointResult.Value.Details);
        var totalCheckpointBytes = await ReadStreamAsync(
            consumer,
            checkpointStream,
            checkpointMetadata,
            complete: false,
            cancellationToken).ConfigureAwait(false);
        LogRead(_logger, totalCheckpointBytes, checkpointClient.BlobContainerName, checkpointClient.Name);

        await ReadWalSegmentsAsync(
            consumer,
            checkpoint.ReplaySegment,
            checkpoint.ReplayOffset,
            firstSegmentResult: null,
            firstSegmentStream: null,
            expectedFormat: checkpointMetadata.Format,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        if (!_currentSegmentExists)
        {
            await EnsureCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        var sealedSegmentId = _currentSegmentId;
        await SealCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);

        var replaySegmentId = checked(sealedSegmentId + 1);
        var replayOffset = 0L;
        var checkpointId = _nextCheckpointId;
        var checkpointName = _checkpointNameFactory(checkpointId);
        if (string.IsNullOrWhiteSpace(checkpointName))
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint name factory returned an empty blob name.");
        }

        var checkpointClient = _checkpointClientFactory(checkpointName);
        if (checkpointClient is null)
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint client factory returned null.");
        }

        using var checkpointStream = new ReadOnlySequenceStream(value);
        var checkpointResult = await checkpointClient.UploadAsync(
            checkpointStream,
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = CreateHttpHeaders(),
                Metadata = CreateCheckpointBlobMetadata(checkpointId, replaySegmentId, replayOffset),
            },
            cancellationToken).ConfigureAwait(false);
        _nextCheckpointId = checked(checkpointId + 1);

        var rootMetadata = CreateCheckpointMarkerMetadata(
            checkpointName,
            checkpointResult.Value.ETag,
            checkpointId,
            checkpointStream.Length,
            replaySegmentId,
            replayOffset);
        var metadataResult = await _rootLogClient.SetMetadataAsync(
            rootMetadata,
            new BlobRequestConditions { IfMatch = _rootLogETag },
            cancellationToken).ConfigureAwait(false);

        _rootLogETag = metadataResult.Value.ETag;
        SetCurrentSegment(replaySegmentId, exists: false, eTag: default, appendPosition: 0, blockCount: 0);
        LogReplace(_logger, checkpointClient.BlobContainerName, checkpointClient.Name, checkpointStream.Length);
    }

    private async ValueTask EnsureCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        if (_currentSegmentExists)
        {
            return;
        }

        var response = await _currentLogClient.CreateAsync(
            CreateOptions(new AppendBlobRequestConditions { IfNoneMatch = ETag.All }),
            cancellationToken).ConfigureAwait(false);
        SetCurrentSegment(_currentSegmentId, exists: true, response.Value.ETag, appendPosition: 0, blockCount: 0);
    }

    private async ValueTask RollToNextSegmentAsync(CancellationToken cancellationToken)
    {
        if (_currentSegmentExists)
        {
            await SealCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        SetCurrentSegment(checked(_currentSegmentId + 1), exists: false, eTag: default, appendPosition: 0, blockCount: 0);
    }

    private async ValueTask SealCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        if (!_currentSegmentExists)
        {
            return;
        }

        await _currentLogClient.SealAsync(
            new AppendBlobRequestConditions { IfMatch = _currentSegmentETag },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReadWalSegmentsAsync(
        IJournalStorageConsumer consumer,
        uint startSegmentId,
        long startOffset,
        Response<BlobDownloadStreamingResult>? firstSegmentResult,
        Stream? firstSegmentStream,
        string? expectedFormat,
        CancellationToken cancellationToken)
    {
        var segmentId = startSegmentId;
        var offset = startOffset;
        var consumedFirst = firstSegmentResult is null;
        var metadata = expectedFormat is { Length: > 0 } ? new JournalFileMetadata(expectedFormat) : JournalFileMetadata.Empty;
        while (true)
        {
            Response<BlobDownloadStreamingResult> result;
            Stream stream;
            if (!consumedFirst && firstSegmentResult is not null && firstSegmentStream is not null)
            {
                result = firstSegmentResult;
                stream = firstSegmentStream;
                consumedFirst = true;
            }
            else
            {
                var client = GetWalClient(segmentId);
                try
                {
                    result = await client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (exception.Status is 404)
                {
                    SetCurrentSegment(segmentId, exists: false, eTag: default, appendPosition: 0, blockCount: 0);
                    consumer.Complete(metadata);
                    return;
                }

                stream = result.Value.Content;
            }

            await using (stream.ConfigureAwait(false))
            {
                var details = result.Value.Details;
                var segmentFormat = GetFormatKeyMetadata(details.Metadata);
                EnsureCompatibleFormat(expectedFormat, segmentFormat, segmentId);
                metadata = segmentFormat is { Length: > 0 } ? new JournalFileMetadata(segmentFormat) : metadata;
                if (offset > details.ContentLength)
                {
                    throw new InvalidOperationException(
                        $"Azure Blob journal checkpoint points to offset {offset:N0} in WAL segment {_walNameFactory(segmentId)}, but the segment length is {details.ContentLength:N0}.");
                }

                if (offset > 0)
                {
                    await SkipExactlyAsync(stream, offset, cancellationToken).ConfigureAwait(false);
                }

                var bytesRead = await ReadStreamAsync(consumer, stream, metadata, complete: false, cancellationToken).ConfigureAwait(false);
                LogRead(_logger, bytesRead, _rootLogClient.BlobContainerName, _walNameFactory(segmentId));

                if (!details.IsSealed)
                {
                    SetCurrentSegment(segmentId, exists: true, details.ETag, details.ContentLength, details.BlobCommittedBlockCount);
                    consumer.Complete(metadata);
                    return;
                }
            }

            segmentId = checked(segmentId + 1);
            offset = 0;
        }
    }

    private void SetCurrentSegment(uint segmentId, bool exists, ETag eTag, long appendPosition, int blockCount)
    {
        _currentSegmentId = segmentId;
        _currentLogClient = GetWalClient(segmentId);
        _currentSegmentExists = exists;
        _currentSegmentETag = eTag;
        _appendPosition = appendPosition;
        _numBlocks = blockCount;
        if (segmentId == 0)
        {
            _rootLogETag = eTag;
        }
    }

    private AppendBlobClient GetWalClient(uint segmentId)
    {
        if (segmentId == 0)
        {
            return _rootLogClient;
        }

        var client = _walClientFactory(segmentId);
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal WAL client factory returned null.");
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

    private static Func<uint, string> CreateDefaultWalNameFactory(AppendBlobClient client)
        => segmentId => segmentId == 0 ? client.Name : $"{client.Name}.log.{segmentId:X8}";

    private static Func<uint, AppendBlobClient> CreateDefaultWalClientFactory(AppendBlobClient client)
        => segmentId => segmentId == 0 ? client : client.GetParentBlobContainerClient().GetAppendBlobClient($"{client.Name}.log.{segmentId:X8}");

    private static Func<uint, string> CreateDefaultCheckpointNameFactory(AppendBlobClient client)
        => checkpointId => $"{client.Name}.checkpoint.{checkpointId:X8}";

    private static Func<string, BlockBlobClient> CreateDefaultCheckpointClientFactory(AppendBlobClient client)
        => checkpointName => client.GetParentBlobContainerClient().GetBlockBlobClient(checkpointName);

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

    private Dictionary<string, string> CreateCheckpointBlobMetadata(uint checkpointId, uint replaySegmentId, long replayOffset)
    {
        var metadata = CreateMetadataDictionary();
        metadata[CheckpointIdMetadataKey] = FormatUInt32(checkpointId);
        metadata[CheckpointReplaySegmentMetadataKey] = FormatUInt32(replaySegmentId);
        metadata[CheckpointReplayOffsetMetadataKey] = replayOffset.ToString(CultureInfo.InvariantCulture);
        return metadata;
    }

    private Dictionary<string, string> CreateCheckpointMarkerMetadata(
        string checkpointName,
        ETag checkpointETag,
        uint checkpointId,
        long checkpointLength,
        uint replaySegmentId,
        long replayOffset)
    {
        var metadata = CreateCheckpointBlobMetadata(checkpointId, replaySegmentId, replayOffset);
        metadata[CheckpointMetadataKey] = checkpointName;
        metadata[CheckpointETagMetadataKey] = checkpointETag.ToString();
        metadata[CheckpointLengthMetadataKey] = checkpointLength.ToString(CultureInfo.InvariantCulture);
        if (_journalFormatKey is { Length: > 0 })
        {
            metadata[CheckpointFormatMetadataKey] = _journalFormatKey;
        }

        return metadata;
    }

    private static string? GetFormatKeyMetadata(IDictionary<string, string>? metadata)
        => metadata is not null
            && metadata.TryGetValue(FormatMetadataKey, out var storedKey)
            && storedKey is { Length: > 0 }
                ? storedKey
                : null;

    private static CheckpointReference? CreateCheckpointReference(IDictionary<string, string>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(CheckpointMetadataKey, out var checkpointName) || checkpointName is not { Length: > 0 })
        {
            return null;
        }

        if (!metadata.TryGetValue(CheckpointETagMetadataKey, out var checkpointETag) || checkpointETag is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references checkpoint blob '{checkpointName}' but does not include '{CheckpointETagMetadataKey}' metadata.");
        }

        if (!TryGetUInt32Metadata(metadata, CheckpointIdMetadataKey, out var checkpointId))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references checkpoint blob '{checkpointName}' but does not include valid '{CheckpointIdMetadataKey}' metadata.");
        }

        if (!metadata.TryGetValue(CheckpointLengthMetadataKey, out var checkpointLengthValue)
            || !long.TryParse(checkpointLengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var checkpointLength)
            || checkpointLength < 0)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references checkpoint blob '{checkpointName}' but does not include valid '{CheckpointLengthMetadataKey}' metadata.");
        }

        if (!TryGetUInt32Metadata(metadata, CheckpointReplaySegmentMetadataKey, out var replaySegment))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references checkpoint blob '{checkpointName}' but does not include valid '{CheckpointReplaySegmentMetadataKey}' metadata.");
        }

        if (!metadata.TryGetValue(CheckpointReplayOffsetMetadataKey, out var replayOffsetValue)
            || !long.TryParse(replayOffsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out var replayOffset)
            || replayOffset < 0)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal marker references checkpoint blob '{checkpointName}' but does not include valid '{CheckpointReplayOffsetMetadataKey}' metadata.");
        }

        var checkpointFormat = metadata.TryGetValue(CheckpointFormatMetadataKey, out var storedCheckpointFormat)
            && storedCheckpointFormat is { Length: > 0 }
                ? storedCheckpointFormat
                : null;

        return new CheckpointReference(checkpointName, new ETag(checkpointETag), checkpointId, checkpointFormat, checkpointLength, replaySegment, replayOffset);
    }

    private IJournalFileMetadata ValidateCheckpointMetadata(CheckpointReference checkpoint, BlobDownloadDetails checkpointDetails)
    {
        if (checkpointDetails.ContentLength != checkpoint.Length)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' length is {checkpointDetails.ContentLength}, but marker metadata expected {checkpoint.Length}.");
        }

        if (!TryGetUInt32Metadata(checkpointDetails.Metadata, CheckpointIdMetadataKey, out var checkpointId) || checkpointId != checkpoint.Id)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' does not include the expected checkpoint id '{FormatUInt32(checkpoint.Id)}'.");
        }

        if (!TryGetUInt32Metadata(checkpointDetails.Metadata, CheckpointReplaySegmentMetadataKey, out var replaySegment) || replaySegment != checkpoint.ReplaySegment)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' does not include the expected replay segment '{FormatUInt32(checkpoint.ReplaySegment)}'.");
        }

        if (!checkpointDetails.Metadata.TryGetValue(CheckpointReplayOffsetMetadataKey, out var replayOffsetValue)
            || !long.TryParse(replayOffsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out var replayOffset)
            || replayOffset != checkpoint.ReplayOffset)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' does not include the expected replay offset '{checkpoint.ReplayOffset}'.");
        }

        var checkpointBlobFormat = GetFormatKeyMetadata(checkpointDetails.Metadata);
        if (checkpoint.Format is { } markerCheckpointFormat
            && checkpointBlobFormat is { }
            && !string.Equals(markerCheckpointFormat, checkpointBlobFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' format metadata is '{checkpointBlobFormat}', but marker metadata expected '{markerCheckpointFormat}'.");
        }

        var effectiveCheckpointFormat = checkpoint.Format ?? checkpointBlobFormat;
        if (checkpoint.Format is { } && checkpointBlobFormat is null)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' does not include format metadata.");
        }

        _nextCheckpointId = Math.Max(_nextCheckpointId, checked(checkpoint.Id + 1));
        return effectiveCheckpointFormat is { } format
            ? new JournalFileMetadata(format)
            : JournalFileMetadata.Empty;
    }

    private static void EnsureCompatibleFormat(string? expectedFormat, string? segmentFormat, uint segmentId)
    {
        if (expectedFormat is null || segmentFormat is null || string.Equals(expectedFormat, segmentFormat, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Azure Blob journal WAL segment {FormatUInt32(segmentId)} format metadata is '{segmentFormat}', but recovery expected '{expectedFormat}'.");
    }

    private static bool TryGetUInt32Metadata(IDictionary<string, string>? metadata, string key, out uint value)
    {
        value = default;
        return metadata is not null
            && metadata.TryGetValue(key, out var text)
            && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatUInt32(uint value) => value.ToString("X8", CultureInfo.InvariantCulture);

    private static bool IsBlobSealed(RequestFailedException exception)
        => exception.Status == 409
            && (string.Equals(exception.ErrorCode, "BlobIsSealed", StringComparison.Ordinal)
                || exception.Message.Contains("sealed", StringComparison.OrdinalIgnoreCase));

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

    private static async ValueTask SkipExactlyAsync(Stream stream, long bytesToSkip, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (bytesToSkip > 0)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytesToSkip)), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new InvalidOperationException("The stream ended before the requested WAL replay offset was reached.");
                }

                bytesToSkip -= bytesRead;
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

    private sealed record CheckpointReference(string Name, ETag ETag, uint Id, string? Format, long Length, uint ReplaySegment, long ReplayOffset);

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
            ThrowIfDisposed();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
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
            ThrowIfDisposed();
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReadOnlySequenceStream));
            }
        }
    }
}
