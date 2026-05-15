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
    private const int MaxSkipBufferBytes = 16 * 1024;

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
        // Appends are written as one Azure append block, so validate blob limits before touching storage.
        ThrowIfBatchTooLarge(value.Length);

        // Ensure local state has the current WAL ETag and manifest before making a conditional append.
        await EnsureWalAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfCompactionRequired();

        using var stream = new ReadOnlySequenceStream(value);
        try
        {
            // Use the last observed WAL ETag so appends fail if the WAL changed since this instance recovered it.
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

            // Cache Azure's post-append state so the next mutation is guarded by the new ETag and block count.
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
        // Fresh storage instances load the WAL manifest first so delete knows whether a checkpoint is referenced.
        if (!WalExists && !await TryLoadWalStateAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Remember the checkpoint before clearing local WAL state so obsolete checkpoint cleanup can still run.
        var checkpointName = _checkpointName;
        try
        {
            // Delete the WAL under its ETag before checkpoint cleanup so a racing WAL update cannot lose its checkpoint.
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
            // Checkpoint cleanup happens after WAL deletion because without the WAL manifest it is unreachable.
            await DeleteCheckpointIfExistsAsync(checkpointName, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        Response<BlobDownloadStreamingResult> walResult;
        try
        {
            // Download the WAL first because its metadata is the manifest for any checkpoint that must be replayed.
            walResult = await _walClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            // A missing WAL is an empty journal; clear cached state before reporting completion.
            SetWal(eTag: default, blockCount: 0, length: 0, checkpointName: null);
            consumer.Complete(metadata: null);
            return;
        }

        var walDetails = walResult.Value.Details;
        var manifest = CreateWalManifest(walDetails.Metadata);

        // Recovery refreshes the cached ETag, length, block count, and checkpoint pointer from the WAL manifest.
        SetWal(walDetails.ETag, walDetails.BlobCommittedBlockCount, walDetails.ContentLength, manifest.Checkpoint?.Name);

        await using var walStream = walResult.Value.Content;
        var expectedFormat = manifest.Metadata.Format;
        if (manifest.Checkpoint is { } checkpoint)
        {
            var checkpointClient = GetCheckpointClient(checkpoint.Name);
            var checkpointResult = await checkpointClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using var checkpointStream = checkpointResult.Value.Content;

            // Replay the immutable checkpoint first because it represents the compacted prefix before WAL entries.
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

            // The checkpoint already contains WAL bytes through this offset, so replay only the WAL tail.
            await SkipStreamAsync(walStream, checkpointOffset.WalOffset, cancellationToken).ConfigureAwait(false);
        }

        // Prefer WAL format metadata, falling back to the checkpoint when compaction recreated an empty WAL.
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

        // Complete only after checkpoint and WAL tail have streamed as one logical journal.
        consumer.Complete(walMetadata);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        // Compaction publishes through WAL metadata, so first recover or create the WAL whose ETag will be checked.
        await EnsureWalAsync(cancellationToken).ConfigureAwait(false);

        // Snapshot the current publication state; the previous checkpoint is obsolete only after the new WAL wins.
        var walETag = _walETag;
        var previousCheckpointName = _checkpointName;
        using var checkpointStream = new ReadOnlySequenceStream(value);
        while (true)
        {
            // The checkpoint blob is immutable and content-addressed by a random snapshot id for safe retry on collision.
            var checkpointName = GetCheckpointName(Guid.NewGuid().ToString("N"));
            var checkpointClient = GetCheckpointClient(checkpointName);
            try
            {
                // Upload the checkpoint before publishing it from WAL metadata so upload failures leave recovery unchanged.
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
                // Recreate the WAL under its ETag to publish the checkpoint only if the existing WAL is unchanged.
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
                // Keep the previous checkpoint until the new WAL is published so its manifest never points at a missing blob.
                await DeleteCheckpointIfExistsAsync(previousCheckpointName, cancellationToken).ConfigureAwait(false);
            }

            LogReplace(_shared.Logger, checkpointClient.BlobContainerName, checkpointClient.Name, checkpointStream.Length);

            // At this point the new checkpoint is reachable from WAL metadata and old state has been detached.
            return;
        }
    }

    private static void ThrowIfBatchTooLarge(long length)
    {
        // Azure rejects oversize append blocks, so fail locally with the journal-specific guidance.
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
        // Stop before Azure's hard block cap so the caller can compact while there is still append headroom.
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
        // Either create the initial WAL or load the WAL created by a racing instance, then loop until state is cached.
        while (!WalExists)
        {
            try
            {
                // A newly-created WAL has no checkpoint pointer; existing WALs are handled by the conflict path.
                var response = await CreateWalAsync(
                    checkpointName: null,
                    new AppendBlobRequestConditions { IfNoneMatch = ETag.All },
                    cancellationToken).ConfigureAwait(false);
                SetWal(response.Value.ETag, blockCount: 0, length: 0, checkpointName: null);
                return;
            }
            catch (RequestFailedException exception) when (IsBlobAlreadyExists(exception))
            {
                // Another instance created the WAL first; read/discard it to recover its ETag and manifest before appending.
                await ReadAsync(DiscardingJournalStorageConsumer.Instance, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<bool> TryLoadWalStateAsync(CancellationToken cancellationToken)
    {
        Response<BlobDownloadStreamingResult> walResult;
        try
        {
            // Open the WAL just long enough to read properties and metadata; the content is discarded here.
            walResult = await _walClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            // Missing WAL means there is no durable state to delete or mutate.
            SetWal(eTag: default, blockCount: 0, length: 0, checkpointName: null);
            return false;
        }

        await using var content = walResult.Value.Content;
        var walDetails = walResult.Value.Details;
        var manifest = CreateWalManifest(walDetails.Metadata);

        // Cache the mutation preconditions and compaction signals without replaying journal bytes.
        SetWal(walDetails.ETag, walDetails.BlobCommittedBlockCount, walDetails.ContentLength, manifest.Checkpoint?.Name);
        return true;
    }

    private void SetWal(ETag eTag, int blockCount, long length, string? checkpointName)
    {
        // Keep the cached WAL mutation precondition, compaction counters, and checkpoint pointer in sync.
        _walETag = eTag;
        _numBlocks = blockCount;
        _walLength = length;
        _checkpointName = checkpointName;
    }

    private AppendBlobClient GetWalClient()
    {
        // Resolve the provider-specific WAL client once; checkpoint clients are resolved by published name.
        var client = _shared.BlobClientProvider.GetWalClient(_grainContext.GrainId);
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal WAL client provider returned null.");
    }

    private async ValueTask<Response<BlobContentInfo>> CreateWalAsync(
        string? checkpointName,
        AppendBlobRequestConditions conditions,
        CancellationToken cancellationToken)
    {
        // Creating an append blob is also how compaction publishes a fresh WAL manifest.
        return await _walClient.CreateAsync(
            new AppendBlobCreateOptions
            {
                Conditions = conditions,
                HttpHeaders = CreateHttpHeaders(_shared.MimeType),
                Metadata = CreateWalMetadata(checkpointName, checkpointOffset: 0),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static BlobHttpHeaders? CreateHttpHeaders(string? contentType)
    {
        // Content type is optional transport metadata; journal format is carried separately in blob metadata.
        return contentType is { Length: > 0 } ? new BlobHttpHeaders { ContentType = contentType } : null;
    }

    private string GetCheckpointName(string snapshotId)
    {
        // Let the configured provider own checkpoint naming so custom layouts stay consistent with WAL naming.
        var checkpointName = _shared.BlobClientProvider.GetCheckpointName(_grainContext.GrainId, snapshotId);
        if (string.IsNullOrWhiteSpace(checkpointName))
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned an empty blob name.");
        }

        return checkpointName;
    }

    private BlockBlobClient GetCheckpointClient(string checkpointName)
    {
        // Resolve by the published checkpoint name so recovery can open checkpoints created by older instances.
        var client = _shared.BlobClientProvider.GetCheckpointClient(_grainContext.GrainId, checkpointName);
        return client ?? throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned null.");
    }

    private async ValueTask DeleteCheckpointIfExistsAsync(string checkpointName, CancellationToken cancellationToken)
    {
        var checkpointClient = GetCheckpointClient(checkpointName);
        try
        {
            // Obsolete checkpoint cleanup is best-effort because the published WAL no longer references it.
            await checkpointClient.DeleteIfExistsAsync(DeleteSnapshotsOption.None, conditions: null, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception)
        {
            LogCheckpointCleanupFailure(_shared.Logger, checkpointClient.BlobContainerName, checkpointClient.Name, exception);
        }
    }

    private Dictionary<string, string> CreateMetadataDictionary()
    {
        // Start with metadata common to WAL and checkpoint blobs so recovery can verify the journal format.
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
        // WAL metadata is the recovery manifest: common format plus optional checkpoint pointer and WAL offset.
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
        // Decode the WAL manifest, accepting non-compacted WALs that have no checkpoint pointer.
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
        // Refuse to stitch checkpoint and WAL data together if their declared journal formats differ.
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
    {
        // These failures mean our cached WAL view is stale or gone, so the caller must recover before retrying.
        return exception.Status is 404 or 412
            || exception.Status == 409 && string.Equals(exception.ErrorCode, "ConditionNotMet", StringComparison.Ordinal);
    }

    private static async ValueTask<long> ReadStreamAsync(
        IJournalStorageConsumer consumer,
        Stream input,
        IJournalFileMetadata? metadata,
        bool complete,
        CancellationToken cancellationToken)
    {
        // Stream one blob through a reusable buffer; callers decide when the combined logical journal completes.
        metadata ??= JournalFileMetadata.Empty;
        using var buffer = new ArcBufferWriter();
        long totalBytesRead = 0;
        while (true)
        {
            var memory = buffer.GetMemory();
            var bytesRead = await input.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                // ReadAsync passes complete:false because checkpoint data and WAL tail are completed together later.
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

            // Let the consumer drain incrementally so large blobs do not need to be buffered in full.
            ReadBuffer(consumer, buffer, metadata, isCompleted: false);
        }
    }

    private static async ValueTask SkipStreamAsync(Stream input, long length, CancellationToken cancellationToken)
    {
        // Used during recovery to consume checkpoint-covered WAL bytes without exposing them to the consumer.
        if (length <= 0)
        {
            return;
        }

        if (input.CanSeek)
        {
            // Seeking is only used when supported; Azure response streams are often forward-only.
            input.Seek(length, SeekOrigin.Current);
            return;
        }

        // Non-seekable streams can only advance by reading, so drain discarded bytes into a pooled buffer.
        var maxChunkSize = (int)Math.Min(MaxSkipBufferBytes, length);
        var buffer = ArrayPool<byte>.Shared.Rent(maxChunkSize);

        try
        {
            while (length > 0)
            {
                // ArrayPool can return a larger array, so slice it to keep each skip read capped.
                var toRead = (int)Math.Min(maxChunkSize, length);

                // ReadExactlyAsync guarantees the buffer slice is completely filled before returning,
                // or it throws an EndOfStreamException if the stream ends early.
                await input.ReadExactlyAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

                length -= toRead;
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidOperationException("Azure Blob journal WAL ended before the checkpoint offset was reached.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadBuffer(IJournalStorageConsumer consumer, ArcBufferWriter buffer, IJournalFileMetadata metadata, bool isCompleted)
    {
        // The consumer advances the reader side of the buffer, leaving unread bytes to be detected by the caller.
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

    private readonly record struct CheckpointReference(string Name, long WalOffset);

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

}
