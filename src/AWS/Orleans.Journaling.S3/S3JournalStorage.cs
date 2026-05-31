using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace Orleans.Journaling;

internal sealed partial class S3JournalStorage : IJournalStorage
{
    internal const string FormatMetadataKey = "format";
    internal const string CheckpointMetadataKey = "checkpoint";
    internal const string CheckpointOffsetMetadataKey = "checkpoint-offset";
    internal const string WalGenerationMetadataKey = "wal-generation";

    private const string MetadataHeaderPrefix = "x-amz-meta-";
    private const int MaxAppendPartsPerObject = 10_000;
    private const int HeadroomPartCount = 100;
    private const int RequestCompactionPartCount = 9_800;
    private const int CompactedWalMarkerBytes = 16;

    private readonly S3JournalStorageShared _shared;
    private readonly IAmazonS3 _client;
    private readonly JournalId _journalId;
    private readonly string _walObjectKey;
    private int _numParts;
    private string? _walETag;
    private WalProviderState _walProviderState;

    private bool WalExists => _walETag is not null;

    public bool IsCompactionRequested => _numParts > RequestCompactionPartCount;

    internal S3JournalStorage(S3JournalStorageShared shared, IAmazonS3 client, JournalId journalId)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(client);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        _shared = shared;
        _client = client;
        _journalId = journalId;
        _walObjectKey = GetWalObjectKey();
    }

    public async ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        var callerMetadata = CopyAndValidateCallerMetadata(metadata);
        try
        {
            var created = await CreateWalAsync(
                checkpointName: null,
                ifMatch: null,
                ifNoneMatch: true,
                cancellationToken,
                callerMetadata).ConfigureAwait(false);
            SetWal(created.Response.ETag, created.ProviderState, lastModified: null);
            succeeded = true;
            return true;
        }
        catch (AmazonS3Exception exception) when (IsObjectAlreadyExists(exception))
        {
            succeeded = true;
            return false;
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationCreate,
                Stopwatch.GetElapsedTime(startTimestamp),
                bytes: 0,
                succeeded);
        }
    }

    public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            var properties = await GetPropertiesCoreAsync(expectedETag: null, cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return properties is null ? null : CreateJournalMetadata(properties.ETag, CopyMetadata(properties.Metadata));
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationGetMetadata,
                Stopwatch.GetElapsedTime(startTimestamp),
                bytes: 0,
                succeeded);
        }
    }

    public async ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        var setValues = CopyAndValidateCallerMetadata(set);
        var removeValues = CopyRemove(remove, setValues);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GetObjectMetadataResponse? properties;
                try
                {
                    properties = await GetPropertiesCoreAsync(expectedETag, cancellationToken).ConfigureAwait(false);
                }
                catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed)
                {
                    succeeded = true;
                    return null;
                }

                if (properties is null)
                {
                    succeeded = true;
                    return null;
                }

                var walState = CreateWalState(properties);
                var metadata = CopyMetadata(properties.Metadata);
                if (!ApplyCallerMetadataUpdate(metadata, setValues, removeValues))
                {
                    SetWal(walState.ETag, walState.ProviderState, walState.LastModified);
                    succeeded = true;
                    return CreateJournalMetadata(properties.ETag, metadata);
                }

                var copyRequest = new CopyObjectRequest
                {
                    SourceBucket = _shared.BucketName,
                    SourceKey = _walObjectKey,
                    DestinationBucket = _shared.BucketName,
                    DestinationKey = _walObjectKey,
                    MetadataDirective = S3MetadataDirective.REPLACE,
                    ETagToMatch = expectedETag ?? properties.ETag,
                };
                ApplyObjectHeaders(copyRequest, metadata);

                try
                {
                    var response = await _client.CopyObjectAsync(copyRequest, cancellationToken).ConfigureAwait(false);
                    SetWal(response.ETag, walState.ProviderState, lastModified: null);
                    succeeded = true;
                    return CreateJournalMetadata(response.ETag, metadata);
                }
                catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed)
                {
                    if (expectedETag is not null)
                    {
                        succeeded = true;
                        return null;
                    }
                }
            }

            succeeded = true;
            return null;
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationUpdateMetadata,
                Stopwatch.GetElapsedTime(startTimestamp),
                bytes: 0,
                succeeded);
        }
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (!WalExists)
                {
                    await EnsureWalAsync(cancellationToken).ConfigureAwait(false);
                }

                ThrowIfCompactionRequired();

                var expectedETag = _walETag!;
                var expectedProviderState = _walProviderState;
                try
                {
                    if (_shared.Options.UseS3ExpressAppend)
                    {
                        await AppendWithS3ExpressAsync(value, expectedETag, expectedProviderState, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await AppendWithConditionalRewriteAsync(value, expectedETag, cancellationToken).ConfigureAwait(false);
                    }

                    LogAppend(_shared.Logger, value.Length, _shared.BucketName, _walObjectKey);
                    succeeded = true;
                    return;
                }
                catch (AmazonS3Exception exception) when (IsWalMutationConflict(exception))
                {
                    var refreshed = attempt < _shared.Options.MaxMetadataOnlyConflictRetries
                        ? await RetryAfterMetadataOnlyConflictAsync(attempt, expectedProviderState, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (refreshed is not null)
                    {
                        continue;
                    }

                    throw CreateInconsistentWalStateException(
                        "S3 journal WAL changed while appending; recovery is required.",
                        expectedETag,
                        exception);
                }
            }
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationAppend,
                Stopwatch.GetElapsedTime(startTimestamp),
                value.Length,
                succeeded);
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            WalState? walState;
            var expectedETag = _walETag;
            var expectedProviderState = _walProviderState;
            try
            {
                walState = await TryLoadWalStateAsync(expectedETag, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exception) when (IsWalMutationConflict(exception))
            {
                walState = expectedETag is not null
                    ? await TryRefreshWalStateAfterMetadataOnlyConflictAsync(expectedProviderState, cancellationToken).ConfigureAwait(false)
                    : null;
                if (walState is null)
                {
                    throw CreateInconsistentWalStateException(
                        "S3 journal WAL changed while deleting the journal; recovery is required.",
                        expectedETag,
                        exception);
                }
            }

            if (walState is null)
            {
                if (expectedETag is not null)
                {
                    throw CreateInconsistentWalStateException(
                        "S3 journal WAL changed while deleting the journal; recovery is required.",
                        expectedETag);
                }

                succeeded = true;
                return;
            }

            var checkpointName = walState.Value.Manifest.Checkpoint?.Name;
            for (var attempt = 0; ; attempt++)
            {
                var deleteWalState = walState.Value;
                try
                {
                    var deleteRequest = new DeleteObjectRequest
                    {
                        BucketName = _shared.BucketName,
                        Key = _walObjectKey,
                    };
                    if (_shared.Options.UseConditionalDelete)
                    {
                        deleteRequest.IfMatchSize = deleteWalState.ProviderState.ContentLength;
                        deleteRequest.IfMatchLastModifiedTime = deleteWalState.LastModified;
                    }

                    await _client.DeleteObjectAsync(deleteRequest, cancellationToken).ConfigureAwait(false);
                    SetWal(eTag: null, providerState: default, lastModified: null);
                    break;
                }
                catch (AmazonS3Exception exception) when (IsWalMutationConflict(exception))
                {
                    var refreshed = attempt < _shared.Options.MaxMetadataOnlyConflictRetries
                        ? await RetryAfterMetadataOnlyConflictAsync(attempt, deleteWalState.ProviderState, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (refreshed is { } refreshedState)
                    {
                        walState = refreshedState;
                        checkpointName = refreshedState.Manifest.Checkpoint?.Name;
                        continue;
                    }

                    throw CreateInconsistentWalStateException(
                        "S3 journal WAL changed while deleting the journal; recovery is required.",
                        deleteWalState.ETag,
                        exception);
                }
            }

            if (checkpointName is not null)
            {
                await DeleteCheckpointIfExistsAsync(checkpointName, cancellationToken).ConfigureAwait(false);
            }

            succeeded = true;
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationDelete,
                Stopwatch.GetElapsedTime(startTimestamp),
                bytes: 0,
                succeeded);
        }
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        var bytes = 0L;

        try
        {
            GetObjectResponse walResult;
            try
            {
                walResult = await _client.GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = _shared.BucketName,
                        Key = _walObjectKey,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.NotFound)
            {
                SetWal(eTag: null, providerState: default, lastModified: null);
                consumer.Complete(metadata: null);
                succeeded = true;
                return;
            }

            using (walResult)
            {
                var walMetadata = CopyMetadata(walResult.Metadata);
                var manifest = CreateWalManifest(walMetadata);
                SetWal(walResult.ETag, CreateWalProviderState(manifest, walResult.ContentLength, walResult.PartsCount), walResult.LastModified);

                var expectedFormat = manifest.Metadata.Format;
                if (manifest.Checkpoint is { } checkpoint)
                {
                    using var checkpointResult = await _client.GetObjectAsync(
                        new GetObjectRequest
                        {
                            BucketName = _shared.BucketName,
                            Key = checkpoint.Name,
                        },
                        cancellationToken).ConfigureAwait(false);

                    var checkpointMetadata = ValidateCheckpointMetadata(checkpoint, CopyMetadata(checkpointResult.Metadata), expectedFormat);
                    var totalCheckpointBytes = await consumer.ReadAsync(
                        checkpointResult.ResponseStream,
                        checkpointMetadata,
                        complete: false,
                        cancellationToken).ConfigureAwait(false);
                    LogRead(_shared.Logger, totalCheckpointBytes, _shared.BucketName, checkpoint.Name);
                    bytes += totalCheckpointBytes;
                    expectedFormat = checkpointMetadata.Format;
                }

                if (manifest.Checkpoint is { WalOffset: > 0 } checkpointOffset)
                {
                    if (checkpointOffset.WalOffset > walResult.ContentLength)
                    {
                        throw new InvalidOperationException(
                            $"S3 journal checkpoint offset {checkpointOffset.WalOffset:N0} exceeds WAL length {walResult.ContentLength:N0}.");
                    }

                    await SkipStreamAsync(walResult.ResponseStream, checkpointOffset.WalOffset, cancellationToken).ConfigureAwait(false);
                }

                var metadata = manifest.Metadata.Format is { Length: > 0 }
                    ? manifest.Metadata
                    : expectedFormat is { Length: > 0 }
                        ? new JournalMetadata(expectedFormat)
                        : JournalMetadata.Empty;
                var totalWalBytes = await consumer.ReadAsync(
                    walResult.ResponseStream,
                    metadata,
                    complete: false,
                    cancellationToken).ConfigureAwait(false);
                LogRead(_shared.Logger, totalWalBytes, _shared.BucketName, _walObjectKey);
                bytes += totalWalBytes;

                consumer.Complete(metadata);
            }

            succeeded = true;
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationRead,
                Stopwatch.GetElapsedTime(startTimestamp),
                bytes,
                succeeded);
        }
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            await EnsureWalAsync(cancellationToken).ConfigureAwait(false);

            var expectedWalETag = _walETag!;
            var expectedProviderState = _walProviderState;
            WalState? walState;
            try
            {
                walState = await TryLoadWalStateAsync(expectedWalETag, cancellationToken).ConfigureAwait(false);
                if (walState is null)
                {
                    throw CreateInconsistentWalStateException(
                        "S3 journal WAL changed while publishing a checkpoint; recovery is required.",
                        expectedWalETag);
                }
            }
            catch (AmazonS3Exception exception) when (IsWalMutationConflict(exception))
            {
                walState = await TryRefreshWalStateAfterMetadataOnlyConflictAsync(expectedProviderState, cancellationToken).ConfigureAwait(false);
                if (walState is null)
                {
                    throw CreateInconsistentWalStateException(
                        "S3 journal WAL changed while publishing a checkpoint; recovery is required.",
                        expectedWalETag,
                        exception);
                }
            }

            var previousCheckpointName = _shared.Options.DeleteOldCheckpoints ? walState.Value.Manifest.Checkpoint?.Name : null;

            using var checkpointStream = new ReadOnlySequenceStream(value);
            while (true)
            {
                var checkpointName = GetCheckpointName(Guid.NewGuid().ToString("N"));
                try
                {
                    checkpointStream.Position = 0;
                    var checkpointRequest = CreatePutObjectRequest(
                        checkpointName,
                        checkpointStream,
                        CreateCheckpointObjectMetadata());
                    checkpointRequest.IfNoneMatch = "*";
                    await _client.PutObjectAsync(checkpointRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (AmazonS3Exception exception) when (IsObjectAlreadyExists(exception))
                {
                    continue;
                }

                for (var attempt = 0; ; attempt++)
                {
                    var publishWalState = walState.Value;
                    try
                    {
                        var created = await CreateWalAsync(
                            checkpointName,
                            ifMatch: publishWalState.ETag,
                            ifNoneMatch: false,
                            cancellationToken,
                            publishWalState.Manifest.Metadata.Properties).ConfigureAwait(false);
                        SetWal(created.Response.ETag, created.ProviderState, lastModified: null);
                        break;
                    }
                    catch (AmazonS3Exception exception) when (IsWalMutationConflict(exception))
                    {
                        var refreshed = attempt < _shared.Options.MaxMetadataOnlyConflictRetries
                            ? await RetryAfterMetadataOnlyConflictAsync(attempt, publishWalState.ProviderState, cancellationToken).ConfigureAwait(false)
                            : null;
                        if (refreshed is { } refreshedState)
                        {
                            walState = refreshedState;
                            continue;
                        }

                        throw CreateInconsistentWalStateException(
                            "S3 journal WAL changed while publishing a checkpoint; recovery is required.",
                            publishWalState.ETag,
                            exception);
                    }
                }

                if (previousCheckpointName is not null && !string.Equals(previousCheckpointName, checkpointName, StringComparison.Ordinal))
                {
                    await DeleteCheckpointIfExistsAsync(previousCheckpointName, cancellationToken).ConfigureAwait(false);
                }

                LogReplace(_shared.Logger, _shared.BucketName, checkpointName, checkpointStream.Length);
                succeeded = true;
                return;
            }
        }
        finally
        {
            S3JournalStorageInstruments.OnOperationCompleted(
                S3JournalStorageInstruments.OperationReplace,
                Stopwatch.GetElapsedTime(startTimestamp),
                value.Length,
                succeeded);
        }
    }

    private async ValueTask AppendWithS3ExpressAsync(
        ReadOnlySequence<byte> value,
        string expectedETag,
        WalProviderState expectedProviderState,
        CancellationToken cancellationToken)
    {
        using var stream = new ReadOnlySequenceStream(value);
        var response = await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _shared.BucketName,
                Key = _walObjectKey,
                InputStream = stream,
                AutoCloseStream = false,
                IfMatch = expectedETag,
                WriteOffsetBytes = expectedProviderState.ContentLength,
            },
            cancellationToken).ConfigureAwait(false);

        SetWal(
            response.ETag,
            expectedProviderState with
            {
                ContentLength = expectedProviderState.ContentLength + value.Length,
                PartsCount = expectedProviderState.PartsCount + 1,
            },
            lastModified: null);
    }

    private async ValueTask AppendWithConditionalRewriteAsync(
        ReadOnlySequence<byte> value,
        string expectedETag,
        CancellationToken cancellationToken)
    {
        using var walResult = await _client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = _shared.BucketName,
                Key = _walObjectKey,
                EtagToMatch = expectedETag,
            },
            cancellationToken).ConfigureAwait(false);

        var walMetadata = CopyMetadata(walResult.Metadata);
        var manifest = CreateWalManifest(walMetadata);
        var metadata = CreateWalMetadata(manifest);
        await using var payload = new MemoryStream();
        await walResult.ResponseStream.CopyToAsync(payload, cancellationToken).ConfigureAwait(false);
        foreach (var segment in value)
        {
            await payload.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
        }

        payload.Position = 0;
        var request = CreatePutObjectRequest(_walObjectKey, payload, metadata);
        request.IfMatch = expectedETag;
        var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        SetWal(
            response.ETag,
            CreateWalProviderState(manifest, payload.Length, partsCount: 1),
            lastModified: null);
    }

    private void ThrowIfCompactionRequired()
    {
        if (_numParts < MaxAppendPartsPerObject - HeadroomPartCount)
        {
            return;
        }

        throw new InvalidOperationException(
            $"S3 journal WAL has {_numParts:N0} append parts and must be compacted before more appends. " +
            $"S3 Express One Zone supports at most {MaxAppendPartsPerObject:N0} parts before the object must be copied.");
    }

    private async ValueTask EnsureWalAsync(CancellationToken cancellationToken)
    {
        while (!WalExists)
        {
            try
            {
                var created = await CreateWalAsync(
                    checkpointName: null,
                    ifMatch: null,
                    ifNoneMatch: true,
                    cancellationToken).ConfigureAwait(false);
                SetWal(created.Response.ETag, created.ProviderState, lastModified: null);
                return;
            }
            catch (AmazonS3Exception exception) when (IsObjectAlreadyExists(exception))
            {
                await TryLoadWalStateAsync(expectedETag: null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<WalState?> TryLoadWalStateAsync(
        string? expectedETag,
        CancellationToken cancellationToken,
        bool updateCache = true)
    {
        var walProperties = await GetPropertiesCoreAsync(expectedETag, cancellationToken).ConfigureAwait(false);
        if (walProperties is null)
        {
            if (updateCache)
            {
                SetWal(eTag: null, providerState: default, lastModified: null);
            }

            return null;
        }

        var walState = CreateWalState(walProperties);
        if (updateCache)
        {
            SetWal(walState.ETag, walState.ProviderState, walState.LastModified);
        }

        return walState;
    }

    private async ValueTask<WalState?> RetryAfterMetadataOnlyConflictAsync(
        int attempt,
        WalProviderState expectedProviderState,
        CancellationToken cancellationToken)
    {
        var initial = _shared.Options.MetadataOnlyConflictInitialBackoff;
        if (initial > TimeSpan.Zero)
        {
            var max = _shared.Options.MetadataOnlyConflictMaxBackoff;
            if (max < initial)
            {
                max = initial;
            }

            var multiplier = 1L << Math.Min(attempt, 16);
            var scaledTicks = initial.Ticks * multiplier;
            var cappedTicks = Math.Min(scaledTicks, max.Ticks);
            await Task.Delay(TimeSpan.FromTicks(cappedTicks), cancellationToken).ConfigureAwait(false);
        }

        return await TryRefreshWalStateAfterMetadataOnlyConflictAsync(expectedProviderState, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<WalState?> TryRefreshWalStateAfterMetadataOnlyConflictAsync(
        WalProviderState expectedProviderState,
        CancellationToken cancellationToken)
    {
        if (expectedProviderState.Generation is null)
        {
            return null;
        }

        var walState = await TryLoadWalStateAsync(expectedETag: null, cancellationToken, updateCache: false).ConfigureAwait(false);
        if (walState is null || !IsSameLogicalWal(walState.Value.ProviderState, expectedProviderState))
        {
            return null;
        }

        SetWal(walState.Value.ETag, walState.Value.ProviderState, walState.Value.LastModified);
        return walState;
    }

    private static bool IsSameLogicalWal(WalProviderState left, WalProviderState right)
        => string.Equals(left.Format, right.Format, StringComparison.Ordinal)
            && string.Equals(left.CheckpointName, right.CheckpointName, StringComparison.Ordinal)
            && left.CheckpointOffset == right.CheckpointOffset
            && string.Equals(left.Generation, right.Generation, StringComparison.Ordinal)
            && left.ContentLength == right.ContentLength;

    private void SetWal(string? eTag, WalProviderState providerState, DateTime? lastModified)
    {
        _walETag = eTag;
        _walProviderState = providerState;
        _numParts = providerState.PartsCount;
    }

    private string GetWalObjectKey()
    {
        var objectKey = _shared.Options.GetObjectKeyForJournal(_journalId);
        return S3JournalStorageOptions.GetWalObjectKeyForJournal(_journalId, objectKey);
    }

    private async ValueTask<CreatedWal> CreateWalAsync(
        string? checkpointName,
        string? ifMatch,
        bool ifNoneMatch,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? callerMetadata = null)
    {
        var marker = checkpointName is null ? [] : RandomNumberGenerator.GetBytes(CompactedWalMarkerBytes);
        var metadata = CreateWalMetadata(checkpointName, marker.Length, callerMetadata);
        using var stream = new MemoryStream(marker, writable: false);
        var request = CreatePutObjectRequest(_walObjectKey, stream, metadata);
        request.IfMatch = ifMatch;
        request.IfNoneMatch = ifNoneMatch ? "*" : null;
        var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        var manifest = CreateWalManifest(metadata);
        return new CreatedWal(response, manifest, CreateWalProviderState(manifest, marker.Length, partsCount: marker.Length == 0 ? 0 : 1));
    }

    private PutObjectRequest CreatePutObjectRequest(string key, Stream input, IDictionary<string, string> metadata)
    {
        var request = new PutObjectRequest
        {
            BucketName = _shared.BucketName,
            Key = key,
            InputStream = input,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        };
        ApplyObjectHeaders(request, metadata);
        return request;
    }

    private void ApplyObjectHeaders(PutObjectRequest request, IDictionary<string, string> metadata)
    {
        if (_shared.MimeType is { Length: > 0 })
        {
            request.ContentType = _shared.MimeType;
        }

        if (_shared.Options.StorageClass is { } storageClass)
        {
            request.StorageClass = storageClass;
        }

        AddMetadata(request.Metadata, metadata);
    }

    private void ApplyObjectHeaders(CopyObjectRequest request, IDictionary<string, string> metadata)
    {
        if (_shared.MimeType is { Length: > 0 })
        {
            request.ContentType = _shared.MimeType;
        }

        if (_shared.Options.StorageClass is { } storageClass)
        {
            request.StorageClass = storageClass;
        }

        AddMetadata(request.Metadata, metadata);
    }

    private string GetCheckpointName(string snapshotId)
    {
        var journalObjectKey = _shared.Options.GetObjectKeyForJournal(_journalId);
        return S3JournalStorageOptions.GetCheckpointObjectKeyForJournal(_journalId, journalObjectKey, snapshotId);
    }

    private async ValueTask DeleteCheckpointIfExistsAsync(string checkpointName, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = _shared.BucketName,
                    Key = checkpointName,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception)
        {
            LogCheckpointCleanupFailure(_shared.Logger, _shared.BucketName, checkpointName, exception);
        }
    }

    private Dictionary<string, string> CreateObjectMetadata(string? format)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formatKey = format ?? _shared.JournalFormatKey;
        if (formatKey is { Length: > 0 })
        {
            metadata[FormatMetadataKey] = formatKey;
        }

        return metadata;
    }

    private Dictionary<string, string> CreateCheckpointObjectMetadata() => CreateObjectMetadata(_shared.JournalFormatKey);

    private Dictionary<string, string> CreateWalMetadata(
        string? checkpointName,
        long checkpointOffset,
        IReadOnlyDictionary<string, string>? callerMetadata = null)
    {
        var metadata = CreateObjectMetadata(_shared.JournalFormatKey);
        metadata[WalGenerationMetadataKey] = Guid.NewGuid().ToString("N");
        if (callerMetadata is not null)
        {
            foreach (var (key, value) in callerMetadata)
            {
                ValidateCallerMetadataProperty(key, value);
                metadata[NormalizeMetadataKey(key)] = value;
            }
        }

        if (checkpointName is not null)
        {
            metadata[CheckpointMetadataKey] = checkpointName;
            metadata[CheckpointOffsetMetadataKey] = checkpointOffset.ToString(CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private Dictionary<string, string> CreateWalMetadata(WalManifest manifest)
    {
        var metadata = CreateObjectMetadata(manifest.Metadata.Format);
        if (manifest.Generation is { Length: > 0 })
        {
            metadata[WalGenerationMetadataKey] = manifest.Generation;
        }

        foreach (var (key, value) in manifest.Metadata.Properties)
        {
            metadata[NormalizeMetadataKey(key)] = value;
        }

        if (manifest.Checkpoint is { } checkpoint)
        {
            metadata[CheckpointMetadataKey] = checkpoint.Name;
            metadata[CheckpointOffsetMetadataKey] = checkpoint.WalOffset.ToString(CultureInfo.InvariantCulture);
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
        var fileMetadata = CreateJournalMetadata(eTag: null, metadata);
        var generation = metadata is not null
            && metadata.TryGetValue(WalGenerationMetadataKey, out var storedGeneration)
            && storedGeneration is { Length: > 0 }
                ? storedGeneration
                : null;
        if (metadata is null || !metadata.TryGetValue(CheckpointMetadataKey, out var checkpointName) || checkpointName is not { Length: > 0 })
        {
            return new WalManifest(fileMetadata, Checkpoint: null, generation);
        }

        var checkpointOffset = 0L;
        if (metadata.TryGetValue(CheckpointOffsetMetadataKey, out var checkpointOffsetValue)
            && checkpointOffsetValue is { Length: > 0 }
            && (!long.TryParse(checkpointOffsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out checkpointOffset) || checkpointOffset < 0))
        {
            throw new InvalidOperationException(
                $"S3 journal checkpoint offset metadata is invalid: '{checkpointOffsetValue}'.");
        }

        return new WalManifest(fileMetadata, new CheckpointReference(checkpointName, checkpointOffset), generation);
    }

    private static WalState CreateWalState(GetObjectMetadataResponse properties)
    {
        var metadata = CopyMetadata(properties.Metadata);
        var manifest = CreateWalManifest(metadata);
        return new WalState(
            properties.ETag,
            properties.LastModified,
            manifest,
            CreateWalProviderState(manifest, properties.ContentLength, properties.PartsCount));
    }

    private static WalProviderState CreateWalProviderState(WalManifest manifest, long contentLength, int? partsCount)
        => new(
            manifest.Metadata.Format,
            manifest.Checkpoint?.Name,
            manifest.Checkpoint?.WalOffset ?? 0,
            manifest.Generation,
            contentLength,
            Math.Max(0, partsCount ?? (contentLength == 0 ? 0 : 1)));

    private static IJournalMetadata ValidateCheckpointMetadata(
        CheckpointReference checkpoint,
        IDictionary<string, string> checkpointMetadata,
        string? expectedFormat)
    {
        var checkpointObjectFormat = GetFormatKeyMetadata(checkpointMetadata);
        if (expectedFormat is { Length: > 0 })
        {
            if (checkpointObjectFormat is null)
            {
                throw new InvalidOperationException(
                    $"S3 journal checkpoint '{checkpoint.Name}' does not include format metadata.");
            }

            if (!string.Equals(expectedFormat, checkpointObjectFormat, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"S3 journal checkpoint '{checkpoint.Name}' format metadata is '{checkpointObjectFormat}', but recovery expected '{expectedFormat}'.");
            }
        }

        return CreateJournalMetadata(eTag: null, checkpointMetadata);
    }

    private async ValueTask<GetObjectMetadataResponse?> GetPropertiesCoreAsync(
        string? expectedETag,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _shared.BucketName,
                    Key = _walObjectKey,
                    EtagToMatch = expectedETag,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static IJournalMetadata CreateJournalMetadata(string? eTag, IDictionary<string, string>? metadata)
        => new JournalMetadata(GetFormatKeyMetadata(metadata), eTag, CopyCallerMetadata(metadata));

    private static Dictionary<string, string> CopyCallerMetadata(IDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach (var (rawKey, value) in metadata)
        {
            var key = NormalizeMetadataKey(rawKey);
            if (IsProviderMetadataKey(key))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static Dictionary<string, string> CopyAndValidateCallerMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach (var (rawKey, value) in metadata)
        {
            var key = NormalizeMetadataKey(rawKey);
            ValidateCallerMetadataProperty(key, value);
            result.Add(key, value);
        }

        return result;
    }

    private static Dictionary<string, string> CopyMetadata(MetadataCollection? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata is null)
        {
            return result;
        }

        foreach (var rawKey in metadata.Keys)
        {
            var key = NormalizeMetadataKey(rawKey);
            result[key] = metadata[rawKey];
        }

        return result;
    }

    private static Dictionary<string, string> CopyMetadata(IDictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> set)
    {
        if (remove is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPropertyName in remove)
        {
            var propertyName = NormalizeMetadataKey(rawPropertyName);
            ValidateCallerMetadataPropertyName(propertyName);
            if (set.ContainsKey(propertyName))
            {
                throw new ArgumentException($"Journal metadata property '{propertyName}' cannot be both set and removed.", nameof(remove));
            }

            result.Add(propertyName);
        }

        return result;
    }

    private static bool ApplyCallerMetadataUpdate(
        Dictionary<string, string> metadata,
        IReadOnlyDictionary<string, string> set,
        IReadOnlySet<string> remove)
    {
        var changed = false;
        foreach (var propertyName in remove)
        {
            ValidateCallerMetadataPropertyName(propertyName);
            changed |= metadata.Remove(propertyName);
        }

        foreach (var (propertyName, value) in set)
        {
            ValidateCallerMetadataProperty(propertyName, value);
            if (!metadata.TryGetValue(propertyName, out var currentValue)
                || !string.Equals(currentValue, value, StringComparison.Ordinal))
            {
                metadata[propertyName] = value;
                changed = true;
            }
        }

        return changed;
    }

    private static void AddMetadata(MetadataCollection target, IDictionary<string, string> metadata)
    {
        foreach (var (rawKey, value) in metadata)
        {
            target.Add(NormalizeMetadataKey(rawKey), value);
        }
    }

    private static void ValidateCallerMetadataProperty(string key, string value)
    {
        ValidateCallerMetadataPropertyName(key);
        ArgumentNullException.ThrowIfNull(value);
    }

    private static void ValidateCallerMetadataPropertyName(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal metadata property names must not contain null characters.", nameof(key));
        }

        if (IsProviderMetadataKey(key))
        {
            throw new ArgumentException($"Journal metadata property '{key}' is provider-owned.", nameof(key));
        }
    }

    private static bool IsProviderMetadataKey(string key)
    {
        key = NormalizeMetadataKey(key);
        return string.Equals(key, FormatMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, CheckpointMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, CheckpointOffsetMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, WalGenerationMetadataKey, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("$", StringComparison.Ordinal);
    }

    private static string NormalizeMetadataKey(string key)
        => key.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase)
            ? key[MetadataHeaderPrefix.Length..]
            : key;

    private static bool IsObjectAlreadyExists(AmazonS3Exception exception)
        => exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;

    private static bool IsWalMutationConflict(AmazonS3Exception exception)
        => exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;

    private static InconsistentStateException CreateInconsistentWalStateException(string message, string? expectedETag, Exception? exception = null)
    {
        var currentETag = expectedETag ?? "Unknown";
        return exception is null
            ? new InconsistentStateException(message, storedEtag: "Unknown", currentEtag: currentETag)
            : new InconsistentStateException(message, storedEtag: "Unknown", currentEtag: currentETag, exception);
    }

    private static async ValueTask SkipStreamAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[Math.Min(81920, count)];
        var remaining = count;
        while (remaining > 0)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException($"Unable to skip {count:N0} WAL bytes because the stream ended early.");
            }

            remaining -= bytesRead;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Appended {Length} bytes to S3 object \"{BucketName}/{ObjectKey}\"")]
    private static partial void LogAppend(ILogger logger, long length, string bucketName, string objectKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Read {Length} bytes from S3 object \"{BucketName}/{ObjectKey}\"")]
    private static partial void LogRead(ILogger logger, long length, string bucketName, string objectKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Wrote checkpoint S3 object \"{BucketName}/{ObjectKey}\" containing {Length} bytes")]
    private static partial void LogReplace(ILogger logger, string bucketName, string objectKey, long length);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete obsolete S3 journal checkpoint \"{BucketName}/{ObjectKey}\"")]
    private static partial void LogCheckpointCleanupFailure(ILogger logger, string bucketName, string objectKey, Exception exception);

    private sealed record WalManifest(IJournalMetadata Metadata, CheckpointReference? Checkpoint, string? Generation);

    private readonly record struct WalProviderState(
        string? Format,
        string? CheckpointName,
        long CheckpointOffset,
        string? Generation,
        long ContentLength,
        int PartsCount);

    private readonly record struct WalState(string ETag, DateTime? LastModified, WalManifest Manifest, WalProviderState ProviderState);

    private readonly record struct CreatedWal(PutObjectResponse Response, WalManifest Manifest, WalProviderState ProviderState);

    private readonly record struct CheckpointReference(string Name, long WalOffset);

    internal sealed class S3JournalStorageShared
    {
        public S3JournalStorageShared(
            ILogger<S3JournalStorage> logger,
            IOptions<S3JournalStorageOptions> options,
            string? mimeType = null,
            string? journalFormatKey = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(options);

            Logger = logger;
            Options = options.Value;
            ArgumentNullException.ThrowIfNull(Options);
            if (string.IsNullOrWhiteSpace(Options.BucketName))
            {
                throw new InvalidOperationException($"{nameof(S3JournalStorageOptions.BucketName)} must be configured.");
            }

            BucketName = Options.BucketName;
            MimeType = mimeType;
            JournalFormatKey = journalFormatKey;
        }

        public ILogger<S3JournalStorage> Logger { get; }

        public S3JournalStorageOptions Options { get; }

        public string BucketName { get; }

        public string? MimeType { get; }

        public string? JournalFormatKey { get; }
    }
}
