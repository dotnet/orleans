using System.Buffers;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace Orleans.Journaling;

internal sealed partial class AzureBlobJournalStorage : IJournalStorage
{
    // WAL and checkpoint blobs use this metadata key to declare which journal format their bytes contain.
    internal const string FormatMetadataKey = "format";

    // WAL metadata uses this key to point recovery at the checkpoint blob holding the compacted journal prefix.
    internal const string CheckpointMetadataKey = "checkpoint";

    // WAL metadata uses this key to record how many WAL bytes are already included in the checkpoint; recovery skips that prefix before replaying the WAL tail.
    internal const string CheckpointOffsetMetadataKey = "checkpoint_offset";

    // AppendAsync rejects batches above Azure's documented per-block cap before sending an append request.
    internal const long MaxAppendBlockBytes = 100L * 1024 * 1024;

    // ThrowIfCompactionRequired uses Azure's hard committed-block cap to fail clearly before Azure rejects an append.
    private const int MaxBlocksPerBlob = 50_000;

    // ThrowIfCompactionRequired preserves this many blocks of append capacity after compaction becomes mandatory.
    private const int HeadroomBlockCount = 100;

    // IsCompactionRequested trips at this block count so callers compact before the hard append-blob limit.
    private const int RequestCompactionBlockCount = 49_000;

    private readonly AzureBlobJournalStorageShared _shared;
    private readonly JournalId _journalId;
    private readonly AppendBlobClient _walClient;
    private int _numBlocks;
    private ETag _walETag;

    private bool WalExists => _walETag != default;

    public bool IsCompactionRequested => _numBlocks > RequestCompactionBlockCount;

    internal AzureBlobJournalStorage(
        AzureBlobJournalStorageShared shared,
        JournalId journalId)
    {
        ArgumentNullException.ThrowIfNull(shared);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        _shared = shared;
        _journalId = journalId;
        _walClient = GetWalClient();
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        // Appends are written as one Azure append block, so validate blob limits before touching storage.
        ThrowIfBatchTooLarge(value.Length);

        // Ensure local state has the current WAL ETag and manifest before making a conditional append.
        if (!WalExists)
        {
            await EnsureWalAsync(cancellationToken).ConfigureAwait(false);
        }

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
            SetWal(result.Value.ETag, result.Value.BlobCommittedBlockCount);
        }
        catch (RequestFailedException exception) when (IsBlobSealed(exception))
        {
            throw CreateInconsistentWalStateException(
                "Azure Blob journal WAL is sealed; recovery is required before appending.",
                _walETag,
                exception);
        }
        catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
        {
            throw CreateInconsistentWalStateException(
                "Azure Blob journal WAL changed while appending; recovery is required.",
                _walETag,
                exception);
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var conditions = WalExists ? new BlobRequestConditions { IfMatch = _walETag } : null;
        WalState? walState;
        try
        {
            // Load the WAL manifest only when deletion needs to know which checkpoint may become unreachable.
            walState = await TryLoadWalStateAsync(conditions, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
        {
            throw CreateInconsistentWalStateException(
                "Azure Blob journal WAL changed while deleting the journal; recovery is required.",
                _walETag,
                exception);
        }

        if (walState is null)
        {
            if (conditions is not null)
            {
                throw CreateInconsistentWalStateException(
                    "Azure Blob journal WAL changed while deleting the journal; recovery is required.",
                    _walETag);
            }

            return;
        }

        // Remember the checkpoint before clearing local WAL state so obsolete checkpoint cleanup can still run.
        var checkpointName = walState.Value.Manifest.Checkpoint?.Name;
        try
        {
            // Delete the WAL under its ETag before checkpoint cleanup so a racing WAL update cannot lose its checkpoint.
            await _walClient.DeleteIfExistsAsync(
                DeleteSnapshotsOption.None,
                new BlobRequestConditions { IfMatch = walState.Value.ETag },
                cancellationToken).ConfigureAwait(false);
            SetWal(eTag: default, blockCount: 0);
        }
        catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
        {
            throw CreateInconsistentWalStateException(
                "Azure Blob journal WAL changed while deleting the journal; recovery is required.",
                walState.Value.ETag,
                exception);
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
            SetWal(eTag: default, blockCount: 0);
            consumer.Complete(metadata: null);
            return;
        }

        var walDetails = walResult.Value.Details;
        var manifest = CreateWalManifest(walDetails.Metadata);

        // Recovery refreshes the cached ETag and block count from the WAL manifest.
        SetWal(walDetails.ETag, walDetails.BlobCommittedBlockCount);

        await using var walStream = walResult.Value.Content;
        var expectedFormat = manifest.Metadata.Format;
        if (manifest.Checkpoint is { } checkpoint)
        {
            var checkpointClient = GetCheckpointClient(checkpoint.Name);
            var checkpointResult = await checkpointClient.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using var checkpointStream = checkpointResult.Value.Content;

            // Replay the immutable checkpoint first because it represents the compacted prefix before WAL entries.
            var checkpointMetadata = ValidateCheckpointMetadata(checkpoint, checkpointResult.Value.Details, expectedFormat);
            var totalCheckpointBytes = await consumer.ReadAsync(
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
            await AzureBlobJournalStorageStreamHelpers.SkipStreamAsync(walStream, checkpointOffset.WalOffset, cancellationToken).ConfigureAwait(false);
        }

        // Prefer WAL format metadata, falling back to the checkpoint when compaction recreated an empty WAL.
        var walMetadata = manifest.Metadata.Format is { Length: > 0 }
            ? manifest.Metadata
            : expectedFormat is { Length: > 0 }
                ? new JournalFileMetadata(expectedFormat)
                : JournalFileMetadata.Empty;
        var totalWalBytes = await consumer.ReadAsync(
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

        var expectedWalETag = _walETag;
        string? previousCheckpointName = null;
        if (_shared.Options.DeleteOldCheckpoints)
        {
            // Read the WAL manifest only when cleanup needs the previous checkpoint name, and require the cached ETag to still match.
            WalState? walState;
            try
            {
                walState = await TryLoadWalStateAsync(new BlobRequestConditions { IfMatch = expectedWalETag }, cancellationToken).ConfigureAwait(false);

                if (walState is null)
                {
                    throw CreateInconsistentWalStateException(
                        "Azure Blob journal WAL changed while publishing a checkpoint; recovery is required.",
                        expectedWalETag);
                }
            }
            catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
            {
                throw CreateInconsistentWalStateException(
                    "Azure Blob journal WAL changed while publishing a checkpoint; recovery is required.",
                    expectedWalETag,
                    exception);
            }

            expectedWalETag = walState.Value.ETag;
            previousCheckpointName = walState.Value.Manifest.Checkpoint?.Name;
        }

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
                    new AppendBlobRequestConditions { IfMatch = expectedWalETag },
                    cancellationToken).ConfigureAwait(false);
                SetWal(result.Value.ETag, blockCount: 0);
            }
            catch (RequestFailedException exception) when (IsWalMutationConflict(exception))
            {
                throw CreateInconsistentWalStateException(
                    "Azure Blob journal WAL changed while publishing a checkpoint; recovery is required.",
                    expectedWalETag,
                    exception);
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
                SetWal(response.Value.ETag, blockCount: 0);
                return;
            }
            catch (RequestFailedException exception) when (IsBlobAlreadyExists(exception))
            {
                // Another instance created the WAL first; load only the properties needed before appending.
                await TryLoadWalStateAsync(conditions: null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<WalState?> TryLoadWalStateAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken)
    {
        Response<BlobProperties> walProperties;
        try
        {
            // Read only WAL properties and metadata; no journal bytes are needed to cache mutation state.
            walProperties = await _walClient.GetPropertiesAsync(conditions, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            // Missing WAL means there is no durable state to delete or mutate.
            SetWal(eTag: default, blockCount: 0);
            return null;
        }

        var walDetails = walProperties.Value;
        var manifest = CreateWalManifest(walDetails.Metadata);

        // Cache the mutation precondition and compaction signal without replaying journal bytes.
        SetWal(walDetails.ETag, walDetails.BlobCommittedBlockCount);
        return new WalState(walDetails.ETag, manifest);
    }

    private void SetWal(ETag eTag, int blockCount)
    {
        // Keep the cached WAL mutation precondition and compaction counter in sync.
        _walETag = eTag;
        _numBlocks = blockCount;
    }

    private AppendBlobClient GetWalClient()
    {
        // Resolve the provider-specific WAL client once; checkpoint clients are resolved by published name.
        var client = _shared.BlobClientProvider.GetWalClient(_journalId);
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
        var checkpointName = _shared.BlobClientProvider.GetCheckpointName(_journalId, snapshotId);
        if (string.IsNullOrWhiteSpace(checkpointName))
        {
            throw new InvalidOperationException("The configured Azure Blob journal checkpoint client provider returned an empty blob name.");
        }

        return checkpointName;
    }

    private BlockBlobClient GetCheckpointClient(string checkpointName)
    {
        // Resolve by the published checkpoint name so recovery can open checkpoints created by older instances.
        var client = _shared.BlobClientProvider.GetCheckpointClient(_journalId, checkpointName);
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

    private static InconsistentStateException CreateInconsistentWalStateException(string message, ETag expectedETag, Exception? exception = null)
    {
        var currentETag = expectedETag == default ? "Unknown" : expectedETag.ToString();
        return exception is null
            ? new InconsistentStateException(message, storedEtag: "Unknown", currentEtag: currentETag)
            : new InconsistentStateException(message, storedEtag: "Unknown", currentEtag: currentETag, exception);
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

    private readonly record struct WalState(ETag ETag, WalManifest Manifest);

    private readonly record struct CheckpointReference(string Name, long WalOffset);

    internal sealed class AzureBlobJournalStorageShared
    {
        public AzureBlobJournalStorageShared(
            ILogger<AzureBlobJournalStorage> logger,
            IOptions<AzureBlobJournalStorageOptions> options,
            BlobClientProvider blobClientProvider,
            string? mimeType = null,
            string? journalFormatKey = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(blobClientProvider);

            Logger = logger;
            Options = options.Value;
            MimeType = mimeType;
            JournalFormatKey = journalFormatKey;
            BlobClientProvider = blobClientProvider;
        }

        public ILogger<AzureBlobJournalStorage> Logger { get; }

        public AzureBlobJournalStorageOptions Options { get; }

        public string? MimeType { get; }

        public string? JournalFormatKey { get; }

        public BlobClientProvider BlobClientProvider { get; }
    }

    internal abstract class BlobClientProvider
    {
        public abstract AppendBlobClient GetWalClient(JournalId journalId);

        public abstract string GetCheckpointName(JournalId journalId, string snapshotId);

        public abstract BlockBlobClient GetCheckpointClient(JournalId journalId, string checkpointName);
    }

    internal sealed class OptionsBlobClientProvider(
        IBlobContainerFactory containerFactory,
        AzureBlobJournalStorageOptions options) : BlobClientProvider
    {
        public override AppendBlobClient GetWalClient(JournalId journalId)
            => containerFactory.GetBlobContainerClient(journalId).GetAppendBlobClient(
                AzureBlobJournalStorageOptions.GetWalBlobNameForJournal(journalId, options.GetBlobNameForJournal(journalId)));

        public override string GetCheckpointName(JournalId journalId, string snapshotId)
            => AzureBlobJournalStorageOptions.GetCheckpointBlobNameForJournal(journalId, options.GetBlobNameForJournal(journalId), snapshotId);

        public override BlockBlobClient GetCheckpointClient(JournalId journalId, string checkpointName)
            => containerFactory.GetBlobContainerClient(journalId).GetBlockBlobClient(checkpointName);
    }
}
