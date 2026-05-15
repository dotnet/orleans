using System.Buffers;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed partial class AzureBlobJournalStorage : IJournalStorage
{
    internal const string FormatMetadataKey = "format";
    internal const string GenerationMetadataKey = "generation";
    internal const string CheckpointMetadataKey = "checkpoint";

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
    private readonly BlobClientProvider _blobClientProvider;
    private readonly string? _mimeType;
    private readonly string? _journalFormatKey;
    private readonly ILogger<AzureBlobJournalStorage> _logger;
    private AppendBlobClient _currentLogClient;
    private ulong _currentGeneration;
    private uint _currentSegmentId;
    private long _appendPosition;
    private int _numBlocks;
    private ETag _currentSegmentETag;
    private ETag _rootLogETag;

    private bool CurrentSegmentExists => _currentSegmentETag != default;

    private bool RootExists => _rootLogETag != default;

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
        BlobClientProvider? blobClientProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _rootLogClient = client;
        _currentLogClient = client;
        _blobClientProvider = blobClientProvider ?? DefaultBlobClientProvider.Instance;
        _mimeType = mimeType;
        _journalFormatKey = journalFormatKey;
        _logger = logger;
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        ThrowIfBatchTooLarge(value.Length);
        if (CurrentSegmentExists && _numBlocks >= MaxAppendBlobBlocks - AppendBlobBlockCeilingHeadroom)
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
        if (!RootExists)
        {
            return;
        }

        if (CurrentSegmentExists)
        {
            await SealCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        var generation = checked(_currentGeneration + 1);
        Response<BlobContentInfo> response;
        try
        {
            response = await CreateRootAsync(
                generation,
                checkpointName: null,
                new AppendBlobRequestConditions { IfMatch = _rootLogETag },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (IsRootMutationConflict(exception))
        {
            throw new InvalidOperationException("Azure Blob journal root changed while deleting the journal; recovery is required.", exception);
        }

        _currentGeneration = generation;
        SetCurrentSegment(segmentId: 0, exists: true, response.Value.ETag, appendPosition: 0, blockCount: 0);
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
            _currentGeneration = 0;
            SetCurrentSegment(segmentId: 0, exists: false, eTag: default, appendPosition: 0, blockCount: 0);
            _rootLogETag = default;
            consumer.Complete(metadata: null);
            return;
        }

        await using var rootStream = rootResult.Value.Content;
        _rootLogETag = rootResult.Value.Details.ETag;
        var manifest = CreateRootManifest(rootResult.Value.Details.Metadata);
        _currentGeneration = manifest.Generation;
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
            LogRead(_logger, totalCheckpointBytes, checkpointClient.BlobContainerName, checkpointClient.Name);
            expectedFormat = checkpointMetadata.Format;
        }

        await ReadRootAndSegmentsAsync(
            consumer,
            rootResult.Value.Details,
            rootStream,
            expectedFormat,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        if (!CurrentSegmentExists)
        {
            await EnsureCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        var rootETag = _rootLogETag;
        var generation = checked(_currentGeneration + 1);
        using var checkpointStream = new ReadOnlySequenceStream(value);
        while (true)
        {
            var checkpointName = GetCheckpointName(generation);
            var checkpointClient = GetCheckpointClient(checkpointName);
            try
            {
                checkpointStream.Position = 0;
                await checkpointClient.UploadAsync(
                    checkpointStream,
                    new BlobUploadOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = CreateHttpHeaders(),
                        Metadata = CreateCheckpointBlobMetadata(generation),
                    },
                    cancellationToken).ConfigureAwait(false);

                Response<BlobContentInfo> rootResult;
                try
                {
                    rootResult = await CreateRootAsync(
                        generation,
                        checkpointName,
                        new AppendBlobRequestConditions { IfMatch = rootETag },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (IsRootMutationConflict(exception))
                {
                    throw new InvalidOperationException("Azure Blob journal root changed while publishing a checkpoint; recovery is required.", exception);
                }

                _currentGeneration = generation;
                SetCurrentSegment(segmentId: 0, exists: true, rootResult.Value.ETag, appendPosition: 0, blockCount: 0);
                LogReplace(_logger, checkpointClient.BlobContainerName, checkpointClient.Name, checkpointStream.Length);
                return;
            }
            catch (RequestFailedException exception) when (IsBlobAlreadyExists(exception))
            {
                var currentManifest = await ReadRootManifestForCollisionAsync(cancellationToken).ConfigureAwait(false);
                if (currentManifest?.Checkpoint is { } checkpoint
                    && currentManifest.Generation == generation
                    && string.Equals(checkpoint.Name, checkpointName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Azure Blob journal checkpoint was already published; recovery is required.", exception);
                }

                generation = checked(Math.Max(generation, currentManifest?.Generation ?? _currentGeneration) + 1);
            }
        }
    }

    private async ValueTask EnsureCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        if (CurrentSegmentExists)
        {
            return;
        }

        Response<BlobContentInfo> response;
        if (_currentSegmentId == 0)
        {
            try
            {
                response = await CreateRootAsync(
                    _currentGeneration,
                    checkpointName: null,
                    new AppendBlobRequestConditions { IfNoneMatch = ETag.All },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (IsBlobAlreadyExists(exception))
            {
                await ReadAsync(DiscardingJournalStorageConsumer.Instance, cancellationToken).ConfigureAwait(false);
                if (!CurrentSegmentExists)
                {
                    await EnsureCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

        }
        else
        {
            response = await _currentLogClient.CreateAsync(
                CreateOptions(new AppendBlobRequestConditions { IfNoneMatch = ETag.All }),
                cancellationToken).ConfigureAwait(false);
        }

        SetCurrentSegment(_currentSegmentId, exists: true, response.Value.ETag, appendPosition: 0, blockCount: 0);
    }

    private async ValueTask RollToNextSegmentAsync(CancellationToken cancellationToken)
    {
        if (CurrentSegmentExists)
        {
            await SealCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        SetCurrentSegment(checked(_currentSegmentId + 1), exists: false, eTag: default, appendPosition: 0, blockCount: 0);
    }

    private async ValueTask SealCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        if (!CurrentSegmentExists)
        {
            return;
        }

        var response = await _currentLogClient.SealAsync(
            new AppendBlobRequestConditions { IfMatch = _currentSegmentETag },
            cancellationToken).ConfigureAwait(false);
        _currentSegmentETag = response.Value.ETag;
        if (_currentSegmentId == 0)
        {
            _rootLogETag = response.Value.ETag;
        }
    }

    private async ValueTask ReadRootAndSegmentsAsync(
        IJournalStorageConsumer consumer,
        BlobDownloadDetails rootDetails,
        Stream rootStream,
        string? expectedFormat,
        CancellationToken cancellationToken)
    {
        var rootFormat = GetFormatKeyMetadata(rootDetails.Metadata);
        EnsureCompatibleFormat(expectedFormat, rootFormat, segmentId: 0);
        var metadata = rootFormat is { Length: > 0 }
            ? new JournalFileMetadata(rootFormat)
            : expectedFormat is { Length: > 0 }
                ? new JournalFileMetadata(expectedFormat)
                : JournalFileMetadata.Empty;
        var bytesRead = await ReadStreamAsync(consumer, rootStream, metadata, complete: false, cancellationToken).ConfigureAwait(false);
        LogRead(_logger, bytesRead, _rootLogClient.BlobContainerName, _rootLogClient.Name);

        if (!rootDetails.IsSealed)
        {
            SetCurrentSegment(segmentId: 0, exists: true, rootDetails.ETag, rootDetails.ContentLength, rootDetails.BlobCommittedBlockCount);
            consumer.Complete(metadata);
            return;
        }

        await ReadWalSegmentsAsync(
            consumer,
            startSegmentId: 1,
            startOffset: 0,
            expectedFormat: metadata.Format,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReadWalSegmentsAsync(
        IJournalStorageConsumer consumer,
        uint startSegmentId,
        long startOffset,
        string? expectedFormat,
        CancellationToken cancellationToken)
    {
        var segmentId = startSegmentId;
        var offset = startOffset;
        var metadata = expectedFormat is { Length: > 0 } ? new JournalFileMetadata(expectedFormat) : JournalFileMetadata.Empty;
        while (true)
        {
            Response<BlobDownloadStreamingResult> result;
            Stream stream;
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
            await using (stream.ConfigureAwait(false))
            {
                var details = result.Value.Details;
                var segmentFormat = GetFormatKeyMetadata(details.Metadata);
                EnsureCompatibleFormat(expectedFormat, segmentFormat, segmentId);
                metadata = segmentFormat is { Length: > 0 } ? new JournalFileMetadata(segmentFormat) : metadata;
                if (offset > details.ContentLength)
                {
                    throw new InvalidOperationException(
                        $"Azure Blob journal checkpoint points to offset {offset:N0} in segment {client.Name}, but the segment length is {details.ContentLength:N0}.");
                }

                if (offset > 0)
                {
                    await SkipExactlyAsync(stream, offset, cancellationToken).ConfigureAwait(false);
                }

                var bytesRead = await ReadStreamAsync(consumer, stream, metadata, complete: false, cancellationToken).ConfigureAwait(false);
                LogRead(_logger, bytesRead, _rootLogClient.BlobContainerName, client.Name);

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
        _currentSegmentETag = exists ? eTag : default;
        _appendPosition = appendPosition;
        _numBlocks = blockCount;
        if (segmentId == 0)
        {
            _rootLogETag = exists ? eTag : default;
        }
    }

    private AppendBlobClient GetWalClient(uint segmentId)
    {
        if (segmentId == 0)
        {
            return _rootLogClient;
        }

        var client = _blobClientProvider.GetWalClient(_rootLogClient, _currentGeneration, checked(segmentId - 1));
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal WAL client provider returned null.");
    }

    private AppendBlobCreateOptions CreateOptions(AppendBlobRequestConditions conditions) => new()
    {
        Conditions = conditions,
        HttpHeaders = CreateHttpHeaders(),
        Metadata = CreateSegmentMetadata(),
    };

    private async ValueTask<Response<BlobContentInfo>> CreateRootAsync(
        ulong generation,
        string? checkpointName,
        AppendBlobRequestConditions conditions,
        CancellationToken cancellationToken)
        => await _rootLogClient.CreateAsync(
            new AppendBlobCreateOptions
            {
                Conditions = conditions,
                HttpHeaders = CreateHttpHeaders(),
                Metadata = CreateRootMetadata(generation, checkpointName),
            },
            cancellationToken).ConfigureAwait(false);

    private BlobHttpHeaders? CreateHttpHeaders() => CreateHttpHeaders(_mimeType);

    private static BlobHttpHeaders? CreateHttpHeaders(string? contentType)
        => contentType is { Length: > 0 } ? new BlobHttpHeaders { ContentType = contentType } : null;

    private string GetCheckpointName(ulong generation)
    {
        var checkpointName = _blobClientProvider.GetCheckpointName(_rootLogClient, generation);
        if (string.IsNullOrWhiteSpace(checkpointName))
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned an empty blob name.");
        }

        return checkpointName;
    }

    private BlockBlobClient GetCheckpointClient(string checkpointName)
    {
        var client = _blobClientProvider.GetCheckpointClient(_rootLogClient, checkpointName);
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned null.");
    }

    private static string GetJournalId(string rootName)
        => rootName.EndsWith("/root", StringComparison.Ordinal)
            ? rootName[..^"/root".Length]
            : rootName;

    private Dictionary<string, string> CreateMetadataDictionary(ulong generation)
    {
        var metadata = new Dictionary<string, string>
        {
            [GenerationMetadataKey] = FormatGeneration(generation),
        };
        if (_journalFormatKey is { Length: > 0 })
        {
            metadata[FormatMetadataKey] = _journalFormatKey;
        }

        return metadata;
    }

    private Dictionary<string, string> CreateSegmentMetadata() => CreateMetadataDictionary(_currentGeneration);

    private Dictionary<string, string> CreateCheckpointBlobMetadata(ulong generation) => CreateMetadataDictionary(generation);

    private Dictionary<string, string> CreateRootMetadata(ulong generation, string? checkpointName)
    {
        var metadata = CreateMetadataDictionary(generation);
        if (checkpointName is not null)
        {
            metadata[CheckpointMetadataKey] = checkpointName;
        }

        return metadata;
    }

    private static string? GetFormatKeyMetadata(IDictionary<string, string>? metadata)
        => metadata is not null
            && metadata.TryGetValue(FormatMetadataKey, out var storedKey)
            && storedKey is { Length: > 0 }
                ? storedKey
                : null;

    private RootManifest CreateRootManifest(IDictionary<string, string>? metadata)
    {
        if (!TryGetUInt64Metadata(metadata, GenerationMetadataKey, out var generation))
        {
            throw new InvalidOperationException(
                $"Azure Blob journal root does not include valid '{GenerationMetadataKey}' metadata.");
        }

        var fileMetadata = GetFormatKeyMetadata(metadata) is { } format
            ? new JournalFileMetadata(format)
            : JournalFileMetadata.Empty;
        if (metadata is null || !metadata.TryGetValue(CheckpointMetadataKey, out var checkpointName) || checkpointName is not { Length: > 0 })
        {
            return new RootManifest(generation, fileMetadata, Checkpoint: null);
        }

        return new RootManifest(
            generation,
            fileMetadata,
            new CheckpointReference(checkpointName, generation));
    }

    private async ValueTask<RootManifest?> ReadRootManifestForCollisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _rootLogClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (result.Value.Content.ConfigureAwait(false))
            {
                return CreateRootManifest(result.Value.Details.Metadata);
            }
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            return null;
        }
    }

    private IJournalFileMetadata ValidateCheckpointMetadata(CheckpointReference checkpoint, BlobDownloadDetails checkpointDetails, string? expectedFormat)
    {
        if (!TryGetUInt64Metadata(checkpointDetails.Metadata, GenerationMetadataKey, out var generation) || generation != checkpoint.Generation)
        {
            throw new InvalidOperationException(
                $"Azure Blob journal checkpoint '{checkpoint.Name}' does not include the expected generation '{FormatGeneration(checkpoint.Generation)}'.");
        }

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

    private static void EnsureCompatibleFormat(string? expectedFormat, string? segmentFormat, uint segmentId)
    {
        if (expectedFormat is null || segmentFormat is null || string.Equals(expectedFormat, segmentFormat, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Azure Blob journal WAL segment {FormatUInt32(segmentId)} format metadata is '{segmentFormat}', but recovery expected '{expectedFormat}'.");
    }

    private static bool TryGetUInt64Metadata(IDictionary<string, string>? metadata, string key, out ulong value)
    {
        value = default;
        return metadata is not null
            && metadata.TryGetValue(key, out var text)
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatUInt32(uint value) => value.ToString("X8", CultureInfo.InvariantCulture);

    private static string FormatGeneration(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsBlobSealed(RequestFailedException exception)
        => exception.Status == 409
            && (string.Equals(exception.ErrorCode, "BlobIsSealed", StringComparison.Ordinal)
                || exception.Message.Contains("sealed", StringComparison.OrdinalIgnoreCase));

    private static bool IsBlobAlreadyExists(RequestFailedException exception)
        => exception.Status == 409
            && (string.Equals(exception.ErrorCode, "BlobAlreadyExists", StringComparison.Ordinal)
                || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));

    private static bool IsRootMutationConflict(RequestFailedException exception)
        => exception.Status is 404 or 412
            || (exception.Status == 409 && string.Equals(exception.ErrorCode, "ConditionNotMet", StringComparison.Ordinal));

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

    private sealed record RootManifest(ulong Generation, IJournalFileMetadata Metadata, CheckpointReference? Checkpoint);

    private sealed record CheckpointReference(string Name, ulong Generation);

    internal abstract class BlobClientProvider
    {
        public abstract AppendBlobClient GetWalClient(AppendBlobClient rootLogClient, ulong generation, uint segmentId);

        public abstract string GetCheckpointName(AppendBlobClient rootLogClient, ulong generation);

        public abstract BlockBlobClient GetCheckpointClient(AppendBlobClient rootLogClient, string checkpointName);
    }

    internal sealed class OptionsBlobClientProvider(
        BlobContainerClient containerClient,
        AzureBlobJournalStorageOptions options,
        GrainId grainId) : BlobClientProvider
    {
        public override AppendBlobClient GetWalClient(AppendBlobClient rootLogClient, ulong generation, uint segmentId)
            => containerClient.GetAppendBlobClient(options.GetWalSegmentBlobNameForJournal(grainId, GetJournalId(rootLogClient.Name), generation, segmentId));

        public override string GetCheckpointName(AppendBlobClient rootLogClient, ulong generation)
            => options.GetCheckpointBlobNameForJournal(grainId, GetJournalId(rootLogClient.Name), generation);

        public override BlockBlobClient GetCheckpointClient(AppendBlobClient rootLogClient, string checkpointName)
            => containerClient.GetBlockBlobClient(checkpointName);
    }

    private sealed class DefaultBlobClientProvider : BlobClientProvider
    {
        public static DefaultBlobClientProvider Instance { get; } = new();

        public override AppendBlobClient GetWalClient(AppendBlobClient rootLogClient, ulong generation, uint segmentId)
            => rootLogClient.GetParentBlobContainerClient().GetAppendBlobClient(
                AzureBlobJournalStorageOptions.GetDefaultWalSegmentBlobName(GetJournalId(rootLogClient.Name), generation, segmentId));

        public override string GetCheckpointName(AppendBlobClient rootLogClient, ulong generation)
            => AzureBlobJournalStorageOptions.GetDefaultCheckpointBlobName(GetJournalId(rootLogClient.Name), generation);

        public override BlockBlobClient GetCheckpointClient(AppendBlobClient rootLogClient, string checkpointName)
            => rootLogClient.GetParentBlobContainerClient().GetBlockBlobClient(checkpointName);
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
