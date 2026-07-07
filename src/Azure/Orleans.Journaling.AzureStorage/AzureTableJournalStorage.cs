using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace Orleans.Journaling;

internal sealed partial class AzureTableJournalStorage : IJournalStorage
{
    // Each journal is one table partition. The header row carries the recovery manifest and is the
    // optimistic-concurrency fence: every mutation updates it under its ETag in the same entity
    // group transaction as the data rows it publishes.
    internal const string HeaderRowKey = "$header";

    // The header row uses this property to declare which journal format the data rows contain.
    internal const string FormatPropertyName = "Format";

    // The header row uses this property to point recovery at the current generation of data rows.
    // Generations are random so rows orphaned by a failed replace can never collide with, or be
    // mistaken for, rows of any later generation.
    internal const string GenerationPropertyName = "Generation";

    // The header row uses this property to record how many data rows the current generation contains.
    internal const string RowCountPropertyName = "RowCount";

    // The header row uses this property to record how many journal bytes the current generation contains.
    internal const string LengthPropertyName = "Length";

    // The header row stores caller-owned metadata as a JSON object in this property so caller keys
    // can never collide with provider-owned header properties.
    internal const string MetadataPropertyName = "Metadata";

    // AppendAsync writes all data rows plus the header fence in a single entity group transaction,
    // so the payload must stay well below Azure's 4 MiB transaction limit after Base64 encoding of
    // binary properties.
    internal const long MaxAppendBytes = 2L * 1024 * 1024;

    // Azure Table binary properties hold at most 64 KiB each.
    private const int ChunkBytes = 64 * 1024;

    // 15 chunks keep each data row below Azure's 1 MiB entity limit with headroom for keys and overhead.
    private const int MaxChunksPerEntity = 15;

    // Azure entity group transactions accept at most 100 entities.
    private const int MaxEntitiesPerTransaction = 100;

    private static readonly string[] ChunkPropertyNames = CreateChunkPropertyNames();
    private static readonly string[] RowKeySelect = [nameof(ITableEntity.RowKey)];

    private readonly AzureTableJournalStorageShared _shared;
    private readonly JournalId _journalId;
    private readonly string _partitionKey;
    private TableClient? _tableClient;
    private ETag _headerETag;
    private HeaderProviderState _headerProviderState;

    private bool HeaderExists => _headerETag != default;

    public bool IsCompactionRequested
        => _headerProviderState.RowCount >= _shared.Options.CompactionRowCountThreshold
            || _headerProviderState.Length >= _shared.Options.CompactionSizeThreshold;

    internal AzureTableJournalStorage(
        AzureTableJournalStorageShared shared,
        JournalId journalId)
    {
        ArgumentNullException.ThrowIfNull(shared);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        _shared = shared;
        _journalId = journalId;
        _partitionKey = shared.Options.GetPartitionKeyForJournal(journalId);
    }

    private TableClient Table => _tableClient ??= GetTableClient();

    public async ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;
        var callerMetadata = CopyAndValidateCallerMetadata(metadata);
        try
        {
            var created = await CreateHeaderAsync(callerMetadata, cancellationToken).ConfigureAwait(false);
            if (created.ETag != default)
            {
                SetHeader(created.ETag, created.ProviderState);
            }

            succeeded = true;
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is 409)
        {
            succeeded = true;
            return false;
        }
        finally
        {
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationCreate,
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
            var entity = await GetHeaderEntityAsync(cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return entity is null ? null : CreateJournalMetadata(entity.ETag, entity);
        }
        finally
        {
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationGetMetadata,
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
                var entity = await GetHeaderEntityAsync(cancellationToken).ConfigureAwait(false);
                if (entity is null)
                {
                    succeeded = true;
                    return null;
                }

                if (expectedETag is not null && ToAzureETag(expectedETag) != entity.ETag)
                {
                    succeeded = true;
                    return null;
                }

                var headerState = CreateHeaderState(entity);
                var metadata = headerState.CallerMetadata;
                if (!ApplyCallerMetadataUpdate(metadata, setValues, removeValues))
                {
                    SetHeader(headerState.ETag, headerState.ProviderState);
                    succeeded = true;
                    return CreateJournalMetadata(entity.ETag, entity);
                }

                var patch = new TableEntity(_partitionKey, HeaderRowKey)
                {
                    [MetadataPropertyName] = SerializeCallerMetadata(metadata),
                };

                try
                {
                    var response = await Table.UpdateEntityAsync(
                        patch,
                        expectedETag is null ? entity.ETag : ToAzureETag(expectedETag),
                        TableUpdateMode.Merge,
                        cancellationToken).ConfigureAwait(false);
                    var updatedETag = SetHeaderFromResponse(response, headerState.ProviderState);
                    succeeded = true;
                    return new JournalMetadata(
                        headerState.ProviderState.Format,
                        updatedETag == default ? null : updatedETag.ToString(),
                        metadata);
                }
                catch (RequestFailedException exception) when (exception.Status is 412)
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
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationUpdateMetadata,
                Stopwatch.GetElapsedTime(startTimestamp),
                bytes: 0,
                succeeded);
        }
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        // Appends are written as one entity group transaction, so validate its limits before touching storage.
        ThrowIfBatchTooLarge(value.Length);
        var startTimestamp = Stopwatch.GetTimestamp();
        var succeeded = false;

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                // Ensure local state has the current header ETag and manifest before making a conditional write.
                if (!HeaderExists)
                {
                    await EnsureHeaderAsync(cancellationToken).ConfigureAwait(false);
                }

                var expectedETag = _headerETag;
                var expectedProviderState = _headerProviderState;
                var generation = expectedProviderState.Generation
                    ?? throw new InvalidOperationException("Azure Table journal header state does not include a generation.");
                var entities = CreateDataEntities(value, generation, firstSequence: expectedProviderState.RowCount);
                var newProviderState = expectedProviderState with
                {
                    RowCount = expectedProviderState.RowCount + entities.Count,
                    Length = expectedProviderState.Length + value.Length,
                };

                // Guard the whole batch with the last observed header ETag so appends fail if the
                // journal changed since this instance recovered it.
                var actions = new List<TableTransactionAction>(entities.Count + 1)
                {
                    new(TableTransactionActionType.UpdateMerge, CreateHeaderCountsPatch(newProviderState), expectedETag),
                };
                foreach (var entity in entities)
                {
                    actions.Add(new(TableTransactionActionType.Add, entity));
                }

                try
                {
                    var result = await Table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);

                    LogAppend(_shared.Logger, value.Length, entities.Count, Table.Name, _partitionKey);

                    // Cache the post-append state so the next mutation is guarded by the new header ETag.
                    SetHeaderFromResponse(result.Value[0], newProviderState);
                    succeeded = true;
                    return;
                }
                catch (RequestFailedException exception) when (IsHeaderMutationConflict(exception))
                {
                    var refreshed = attempt < _shared.Options.MaxMetadataOnlyConflictRetries
                        ? await RetryAfterMetadataOnlyConflictAsync(attempt, expectedProviderState, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (refreshed is not null)
                    {
                        continue;
                    }

                    throw CreateInconsistentHeaderStateException(
                        "Azure Table journal header changed while appending; recovery is required.",
                        expectedETag,
                        exception);
                }
            }
        }
        finally
        {
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationAppend,
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
            var expectedETag = _headerETag;
            var expectedProviderState = _headerProviderState;

            // Load the header so deletion is guarded by the current manifest even on fresh instances.
            var headerState = await TryLoadHeaderStateForMutationAsync(expectedETag, expectedProviderState, cancellationToken).ConfigureAwait(false);
            if (headerState is null)
            {
                if (expectedETag != default)
                {
                    throw CreateInconsistentHeaderStateException(
                        "Azure Table journal header changed while deleting the journal; recovery is required.",
                        expectedETag);
                }

                SetHeader(eTag: default, providerState: default);
                succeeded = true;
                return;
            }

            // Capture the row keys before deleting the header so cleanup can never touch rows written
            // by an instance which recreates the journal concurrently: recreated journals use fresh
            // random generations, so their rows are never in this snapshot.
            var rowKeys = await TryCollectRowKeysAsync(CreateAllRowsFilter(), cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                var deleteState = headerState.Value;
                try
                {
                    // Delete the header under its ETag before row cleanup so a racing journal update cannot lose data.
                    await Table.DeleteEntityAsync(_partitionKey, HeaderRowKey, deleteState.ETag, cancellationToken).ConfigureAwait(false);
                    SetHeader(eTag: default, providerState: default);
                    break;
                }
                catch (RequestFailedException exception) when (IsHeaderMutationConflict(exception))
                {
                    var refreshed = attempt < _shared.Options.MaxMetadataOnlyConflictRetries
                        ? await RetryAfterMetadataOnlyConflictAsync(attempt, deleteState.ProviderState, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (refreshed is { } refreshedState)
                    {
                        headerState = refreshedState;
                        continue;
                    }

                    throw CreateInconsistentHeaderStateException(
                        "Azure Table journal header changed while deleting the journal; recovery is required.",
                        deleteState.ETag,
                        exception);
                }
            }

            // Row cleanup happens after header deletion because without the header the rows are unreachable.
            await TryDeleteRowsAsync(rowKeys, cancellationToken).ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationDelete,
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
            // Read the header first because it is the manifest for the generation that must be replayed.
            var entity = await GetHeaderEntityAsync(cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                // A missing header is an empty journal; clear cached state before reporting completion.
                SetHeader(eTag: default, providerState: default);
                consumer.Complete(metadata: null);
                succeeded = true;
                return;
            }

            var headerState = CreateHeaderState(entity);

            // Recovery refreshes the cached ETag and compaction counters from the header manifest.
            SetHeader(headerState.ETag, headerState.ProviderState);

            var chunks = new List<ReadOnlyMemory<byte>>();
            var rowCount = 0L;
            if (headerState.ProviderState is { Generation: { Length: > 0 } generation, RowCount: > 0 and var expectedRowCount })
            {
                // A single range query returns the current generation in sequence order. The upper bound
                // comes from the header so rows appended concurrently by another instance are not read.
                var filter = TableClient.CreateQueryFilter(
                    $"PartitionKey eq {_partitionKey} and RowKey ge {FormatRowKey(generation, 0)} and RowKey le {FormatRowKey(generation, expectedRowCount - 1)}");
                await foreach (var row in Table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    rowCount++;
                    bytes += CollectChunks(row, chunks);
                }
            }

            if (rowCount != headerState.ProviderState.RowCount || bytes != headerState.ProviderState.Length)
            {
                // A concurrent replace can delete rows of the generation being read; the caller must recover.
                throw CreateInconsistentHeaderStateException(
                    "Azure Table journal changed while reading; recovery is required.",
                    headerState.ETag);
            }

            var metadata = new JournalMetadata(headerState.ProviderState.Format, eTag: null, headerState.CallerMetadata);
            consumer.Read(chunks, metadata, complete: true);
            LogRead(_shared.Logger, bytes, Table.Name, _partitionKey);
            succeeded = true;
        }
        finally
        {
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationRead,
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
            // Compaction publishes through the header, so first recover or create the header whose ETag will be checked.
            await EnsureHeaderAsync(cancellationToken).ConfigureAwait(false);

            var expectedETag = _headerETag;
            var expectedProviderState = _headerProviderState;
            var headerState = await TryLoadHeaderStateForMutationAsync(expectedETag, expectedProviderState, cancellationToken).ConfigureAwait(false);
            if (headerState is null)
            {
                throw CreateInconsistentHeaderStateException(
                    "Azure Table journal header changed while replacing the journal; recovery is required.",
                    expectedETag);
            }

            var previousGeneration = _shared.Options.DeleteOldGenerations ? headerState.Value.ProviderState.Generation : null;

            // The new generation is invisible to recovery until the header flip below publishes it, so its
            // rows may span multiple transactions. Rows orphaned by a failure here are unreachable and are
            // removed by a later replace, delete, or row cleanup.
            var newGeneration = Guid.NewGuid().ToString("N");
            var entities = CreateDataEntities(value, newGeneration, firstSequence: 0);
            await SubmitDataEntitiesAsync(entities, cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                var publishState = headerState.Value;
                try
                {
                    // Flip the header under its ETag to publish the new generation only if the journal is unchanged.
                    var response = await Table.UpdateEntityAsync(
                        CreateHeaderFlipPatch(newGeneration, entities.Count, value.Length),
                        publishState.ETag,
                        TableUpdateMode.Merge,
                        cancellationToken).ConfigureAwait(false);
                    SetHeaderFromResponse(
                        response,
                        new HeaderProviderState(NormalizeFormat(_shared.JournalFormatKey), newGeneration, entities.Count, value.Length));
                    break;
                }
                catch (RequestFailedException exception) when (IsHeaderMutationConflict(exception))
                {
                    var refreshed = attempt < _shared.Options.MaxMetadataOnlyConflictRetries
                        ? await RetryAfterMetadataOnlyConflictAsync(attempt, publishState.ProviderState, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (refreshed is { } refreshedState)
                    {
                        headerState = refreshedState;
                        continue;
                    }

                    throw CreateInconsistentHeaderStateException(
                        "Azure Table journal header changed while replacing the journal; recovery is required.",
                        publishState.ETag,
                        exception);
                }
            }

            if (previousGeneration is not null && !string.Equals(previousGeneration, newGeneration, StringComparison.Ordinal))
            {
                // Keep the previous generation until the flip is published so recovery never points at missing rows.
                var previousRowKeys = await TryCollectRowKeysAsync(CreateGenerationRangeFilter(previousGeneration), cancellationToken).ConfigureAwait(false);
                await TryDeleteRowsAsync(previousRowKeys, cancellationToken).ConfigureAwait(false);
            }

            LogReplace(_shared.Logger, newGeneration, value.Length, Table.Name, _partitionKey);
            succeeded = true;
        }
        finally
        {
            _shared.Instruments.OnOperationCompleted(
                AzureTableJournalStorageInstruments.OperationReplace,
                Stopwatch.GetElapsedTime(startTimestamp),
                value.Length,
                succeeded);
        }
    }

    private static void ThrowIfBatchTooLarge(long length)
    {
        // Azure rejects oversize entity group transactions, so fail locally with the journal-specific guidance.
        if (length <= MaxAppendBytes)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Azure Table journal batch of {length:N0} bytes exceeds the per-transaction limit of {MaxAppendBytes:N0} bytes (2 MiB). " +
            "Reduce the operation size or compact more aggressively.");
    }

    private async ValueTask EnsureHeaderAsync(CancellationToken cancellationToken)
    {
        // Either create the initial header or load the header created by a racing instance, then loop until state is cached.
        while (!HeaderExists)
        {
            try
            {
                var created = await CreateHeaderAsync(callerMetadata: null, cancellationToken).ConfigureAwait(false);
                if (created.ETag != default)
                {
                    SetHeader(created.ETag, created.ProviderState);
                    return;
                }

                await TryLoadHeaderStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (IsEntityAlreadyExists(exception))
            {
                // Another instance created the header first; load only the manifest needed before writing.
                await TryLoadHeaderStateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<(ETag ETag, HeaderProviderState ProviderState)> CreateHeaderAsync(
        Dictionary<string, string>? callerMetadata,
        CancellationToken cancellationToken)
    {
        var generation = Guid.NewGuid().ToString("N");
        var entity = new TableEntity(_partitionKey, HeaderRowKey)
        {
            [FormatPropertyName] = _shared.JournalFormatKey ?? string.Empty,
            [GenerationPropertyName] = generation,
            [RowCountPropertyName] = 0L,
            [LengthPropertyName] = 0L,
            [MetadataPropertyName] = SerializeCallerMetadata(callerMetadata),
        };

        var response = await Table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
        return (
            response.Headers.ETag ?? default,
            new HeaderProviderState(NormalizeFormat(_shared.JournalFormatKey), generation, RowCount: 0, Length: 0));
    }

    private async ValueTask<HeaderState?> TryLoadHeaderStateAsync(CancellationToken cancellationToken, bool updateCache = true)
    {
        // Read only the header; no journal bytes are needed to cache mutation state.
        var entity = await GetHeaderEntityAsync(cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            // A missing header means there is no durable state to delete or mutate.
            if (updateCache)
            {
                SetHeader(eTag: default, providerState: default);
            }

            return null;
        }

        var headerState = CreateHeaderState(entity);

        // Cache the mutation precondition and compaction signal without replaying journal bytes.
        if (updateCache)
        {
            SetHeader(headerState.ETag, headerState.ProviderState);
        }

        return headerState;
    }

    private async ValueTask<HeaderState?> TryLoadHeaderStateForMutationAsync(
        ETag expectedETag,
        HeaderProviderState expectedProviderState,
        CancellationToken cancellationToken)
    {
        var headerState = await TryLoadHeaderStateAsync(cancellationToken, updateCache: false).ConfigureAwait(false);
        if (headerState is null)
        {
            return null;
        }

        if (expectedETag != default && headerState.Value.ETag != expectedETag)
        {
            // Accept the new ETag in place only when the change was metadata-only; content changes require recovery.
            if (headerState.Value.ProviderState != expectedProviderState)
            {
                return null;
            }
        }

        SetHeader(headerState.Value.ETag, headerState.Value.ProviderState);
        return headerState;
    }

    private async ValueTask<HeaderState?> RetryAfterMetadataOnlyConflictAsync(
        int attempt,
        HeaderProviderState expectedProviderState,
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

        return await TryRefreshHeaderStateAfterMetadataOnlyConflictAsync(expectedProviderState, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HeaderState?> TryRefreshHeaderStateAfterMetadataOnlyConflictAsync(
        HeaderProviderState expectedProviderState,
        CancellationToken cancellationToken)
    {
        if (expectedProviderState.Generation is null)
        {
            return null;
        }

        var headerState = await TryLoadHeaderStateAsync(cancellationToken, updateCache: false).ConfigureAwait(false);
        if (headerState is null || headerState.Value.ProviderState != expectedProviderState)
        {
            return null;
        }

        SetHeader(headerState.Value.ETag, headerState.Value.ProviderState);
        return headerState;
    }

    private async ValueTask<TableEntity?> GetHeaderEntityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await Table.GetEntityAsync<TableEntity>(
                _partitionKey,
                HeaderRowKey,
                select: null,
                cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            return null;
        }
    }

    private async ValueTask SubmitDataEntitiesAsync(List<TableEntity> entities, CancellationToken cancellationToken)
    {
        var actions = new List<TableTransactionAction>(Math.Min(entities.Count, MaxEntitiesPerTransaction));
        var actionBytes = 0L;
        foreach (var entity in entities)
        {
            var entityBytes = GetEntityPayloadLength(entity);
            if (actions.Count > 0 && (actions.Count == MaxEntitiesPerTransaction || actionBytes + entityBytes > MaxAppendBytes))
            {
                await Table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                actions.Clear();
                actionBytes = 0;
            }

            actions.Add(new(TableTransactionActionType.Add, entity));
            actionBytes += entityBytes;
        }

        if (actions.Count > 0)
        {
            await Table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<List<string>> TryCollectRowKeysAsync(string filter, CancellationToken cancellationToken)
    {
        var rowKeys = new List<string>();
        try
        {
            await foreach (var row in Table.QueryAsync<TableEntity>(filter, select: RowKeySelect, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(row.RowKey, HeaderRowKey, StringComparison.Ordinal))
                {
                    rowKeys.Add(row.RowKey);
                }
            }
        }
        catch (RequestFailedException exception)
        {
            LogRowCleanupFailure(_shared.Logger, Table.Name, _partitionKey, exception);
        }

        return rowKeys;
    }

    private async ValueTask TryDeleteRowsAsync(List<string> rowKeys, CancellationToken cancellationToken)
    {
        try
        {
            // Obsolete row cleanup is best-effort because the published header no longer references the rows.
            var actions = new List<TableTransactionAction>(Math.Min(rowKeys.Count, MaxEntitiesPerTransaction));
            foreach (var rowKey in rowKeys)
            {
                actions.Add(new(TableTransactionActionType.Delete, new TableEntity(_partitionKey, rowKey), ETag.All));
                if (actions.Count == MaxEntitiesPerTransaction)
                {
                    await Table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                    actions.Clear();
                }
            }

            if (actions.Count > 0)
            {
                await Table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException exception)
        {
            LogRowCleanupFailure(_shared.Logger, Table.Name, _partitionKey, exception);
        }
    }

    private string CreateAllRowsFilter()
        => TableClient.CreateQueryFilter($"PartitionKey eq {_partitionKey}");

    private string CreateGenerationRangeFilter(string generation)
    {
        // Row keys are "{generation}-{sequence}", so the exclusive upper bound is the next character after '-'.
        var lowerBound = generation + "-";
        var upperBound = generation + ".";
        return TableClient.CreateQueryFilter($"PartitionKey eq {_partitionKey} and RowKey ge {lowerBound} and RowKey lt {upperBound}");
    }

    private List<TableEntity> CreateDataEntities(ReadOnlySequence<byte> value, string generation, long firstSequence)
    {
        var entities = new List<TableEntity>();
        var remaining = value;
        var sequence = firstSequence;
        while (!remaining.IsEmpty)
        {
            var entity = new TableEntity(_partitionKey, FormatRowKey(generation, sequence++));
            for (var chunk = 0; chunk < MaxChunksPerEntity && !remaining.IsEmpty; chunk++)
            {
                var chunkLength = (int)Math.Min(ChunkBytes, remaining.Length);
                entity[ChunkPropertyNames[chunk]] = remaining.Slice(0, chunkLength).ToArray();
                remaining = remaining.Slice(chunkLength);
            }

            entities.Add(entity);
        }

        return entities;
    }

    private static long GetEntityPayloadLength(TableEntity entity)
    {
        var length = 0L;
        foreach (var propertyName in ChunkPropertyNames)
        {
            if (entity.TryGetValue(propertyName, out var value) && value is byte[] chunk)
            {
                length += chunk.Length;
            }
        }

        return length;
    }

    private static long CollectChunks(TableEntity entity, List<ReadOnlyMemory<byte>> chunks)
    {
        var length = 0L;
        foreach (var propertyName in ChunkPropertyNames)
        {
            if (!entity.TryGetValue(propertyName, out var value))
            {
                break;
            }

            ReadOnlyMemory<byte> chunk = value switch
            {
                byte[] bytes => bytes,
                BinaryData binaryData => binaryData.ToMemory(),
                _ => throw new InvalidOperationException(
                    $"Azure Table journal row \"{entity.RowKey}\" property \"{propertyName}\" is not a binary value."),
            };

            chunks.Add(chunk);
            length += chunk.Length;
        }

        return length;
    }

    private TableEntity CreateHeaderCountsPatch(HeaderProviderState providerState)
        => new(_partitionKey, HeaderRowKey)
        {
            [RowCountPropertyName] = providerState.RowCount,
            [LengthPropertyName] = providerState.Length,
        };

    private TableEntity CreateHeaderFlipPatch(string generation, long rowCount, long length)
        => new(_partitionKey, HeaderRowKey)
        {
            [FormatPropertyName] = _shared.JournalFormatKey ?? string.Empty,
            [GenerationPropertyName] = generation,
            [RowCountPropertyName] = rowCount,
            [LengthPropertyName] = length,
        };

    private void SetHeader(ETag eTag, HeaderProviderState providerState)
    {
        // Keep the cached header mutation precondition and compaction counters in sync.
        _headerETag = eTag;
        _headerProviderState = providerState;
    }

    private ETag SetHeaderFromResponse(Response response, HeaderProviderState providerState)
    {
        // Force a reload before the next mutation if the service did not return the new header ETag.
        if (response.Headers.ETag is { } eTag)
        {
            SetHeader(eTag, providerState);
            return eTag;
        }

        SetHeader(eTag: default, providerState: default);
        return default;
    }

    private TableClient GetTableClient()
    {
        var client = _shared.TableClientProvider.GetTableClient();
        return client ?? throw new InvalidOperationException("The configured Azure Table journal client provider returned null.");
    }

    private static string FormatRowKey(string generation, long sequence)
        => string.Create(CultureInfo.InvariantCulture, $"{generation}-{sequence:D12}");

    private static string[] CreateChunkPropertyNames()
    {
        var names = new string[MaxChunksPerEntity];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = string.Create(CultureInfo.InvariantCulture, $"Data{i:D2}");
        }

        return names;
    }

    private static HeaderState CreateHeaderState(TableEntity entity)
    {
        var generation = entity.GetString(GenerationPropertyName);
        if (generation is not { Length: > 0 })
        {
            throw new InvalidOperationException("Azure Table journal header is missing its generation property.");
        }

        var rowCount = entity.GetInt64(RowCountPropertyName);
        var length = entity.GetInt64(LengthPropertyName);
        if (rowCount is not >= 0 || length is not >= 0)
        {
            throw new InvalidOperationException("Azure Table journal header row count or length properties are invalid.");
        }

        return new HeaderState(
            entity.ETag,
            new HeaderProviderState(NormalizeFormat(entity.GetString(FormatPropertyName)), generation, rowCount.Value, length.Value),
            DeserializeCallerMetadata(entity.GetString(MetadataPropertyName)));
    }

    private IJournalMetadata CreateJournalMetadata(ETag eTag, TableEntity entity)
        => new JournalMetadata(
            NormalizeFormat(entity.GetString(FormatPropertyName)),
            eTag == default ? null : eTag.ToString(),
            DeserializeCallerMetadata(entity.GetString(MetadataPropertyName)));

    private static string? NormalizeFormat(string? format) => format is { Length: > 0 } ? format : null;

    private static string SerializeCallerMetadata(IReadOnlyDictionary<string, string>? metadata)
        => metadata is { Count: > 0 } ? JsonSerializer.Serialize(metadata) : "{}";

    private static Dictionary<string, string> DeserializeCallerMetadata(string? json)
    {
        if (json is not { Length: > 0 })
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Azure Table journal header metadata property is invalid.", exception);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parsed is not null)
        {
            foreach (var (key, value) in parsed)
            {
                result[key] = value;
            }
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

        foreach (var (key, value) in metadata)
        {
            ValidateCallerMetadataProperty(key, value);
            result.Add(key, value);
        }

        return result;
    }

    private static IReadOnlySet<string> CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> set)
    {
        if (remove is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in remove)
        {
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
            changed |= metadata.Remove(propertyName);
        }

        foreach (var (propertyName, value) in set)
        {
            if (!metadata.TryGetValue(propertyName, out var currentValue)
                || !string.Equals(currentValue, value, StringComparison.Ordinal))
            {
                metadata[propertyName] = value;
                changed = true;
            }
        }

        return changed;
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

        if (key.StartsWith("$", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Journal metadata property '{key}' is provider-owned.", nameof(key));
        }
    }

    private static ETag ToAzureETag(string eTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eTag);
        return new ETag(eTag);
    }

    private static bool IsEntityAlreadyExists(RequestFailedException exception)
        => exception.Status == 409
            && (string.Equals(exception.ErrorCode, "EntityAlreadyExists", StringComparison.Ordinal)
                || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns true when an Azure response indicates the journal header has been mutated since our
    /// cached ETag was captured: HTTP 404 (header deleted), HTTP 412 (precondition failed / IfMatch
    /// rejected), or HTTP 409 with <c>EntityAlreadyExists</c> (a competing writer already published
    /// the data row this transaction tried to add). When this returns true, callers should attempt
    /// <see cref="RetryAfterMetadataOnlyConflictAsync"/> to refresh the cached ETag in place when
    /// the change was metadata-only, and otherwise propagate
    /// <see cref="Orleans.Storage.InconsistentStateException"/> to trigger journaling-layer recovery.
    /// Transient transport failures (HTTP 5xx, network errors, timeouts) are handled by the Azure
    /// SDK's built-in retry policy and never reach this classifier.
    /// </summary>
    private static bool IsHeaderMutationConflict(RequestFailedException exception)
    {
        // These failures mean our cached header view is stale or gone, so the caller must recover before retrying.
        return exception.Status is 404 or 412
            || exception.Status == 409 && string.Equals(exception.ErrorCode, "EntityAlreadyExists", StringComparison.Ordinal);
    }

    private static InconsistentStateException CreateInconsistentHeaderStateException(string message, ETag expectedETag, Exception? exception = null)
    {
        var currentETag = expectedETag == default ? "Unknown" : expectedETag.ToString();
        return exception is null
            ? new InconsistentStateException(message, storedEtag: "Unknown", currentEtag: currentETag)
            : new InconsistentStateException(message, storedEtag: "Unknown", currentEtag: currentETag, exception);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Appended {Length} bytes as {RowCount} rows to table \"{TableName}\" partition \"{PartitionKey}\"")]
    private static partial void LogAppend(ILogger logger, long length, int rowCount, string tableName, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Read {Length} bytes from table \"{TableName}\" partition \"{PartitionKey}\"")]
    private static partial void LogRead(ILogger logger, long length, string tableName, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Wrote journal generation \"{Generation}\" containing {Length} bytes to table \"{TableName}\" partition \"{PartitionKey}\"")]
    private static partial void LogReplace(ILogger logger, string generation, long length, string tableName, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete obsolete Azure Table journal rows from table \"{TableName}\" partition \"{PartitionKey}\"")]
    private static partial void LogRowCleanupFailure(ILogger logger, string tableName, string partitionKey, Exception exception);

    private readonly record struct HeaderProviderState(
        string? Format,
        string? Generation,
        long RowCount,
        long Length);

    private readonly record struct HeaderState(
        ETag ETag,
        HeaderProviderState ProviderState,
        Dictionary<string, string> CallerMetadata);

    internal sealed class AzureTableJournalStorageShared
    {
        public AzureTableJournalStorageShared(
            ILogger<AzureTableJournalStorage> logger,
            IOptions<AzureTableJournalStorageOptions> options,
            TableClientProvider tableClientProvider,
            AzureTableJournalStorageInstruments instruments,
            string? journalFormatKey = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(tableClientProvider);

            Logger = logger;
            Options = options.Value;
            ArgumentNullException.ThrowIfNull(Options);
            if (Options.CompactionRowCountThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(AzureTableJournalStorageOptions.CompactionRowCountThreshold)} must be positive.");
            }

            if (Options.CompactionSizeThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(AzureTableJournalStorageOptions.CompactionSizeThreshold)} must be positive.");
            }

            if (Options.MaxMetadataOnlyConflictRetries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(AzureTableJournalStorageOptions.MaxMetadataOnlyConflictRetries)} must be non-negative.");
            }

            if (Options.MetadataOnlyConflictInitialBackoff < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(AzureTableJournalStorageOptions.MetadataOnlyConflictInitialBackoff)} must be non-negative.");
            }

            if (Options.MetadataOnlyConflictMaxBackoff < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(AzureTableJournalStorageOptions.MetadataOnlyConflictMaxBackoff)} must be non-negative.");
            }

            JournalFormatKey = journalFormatKey;
            TableClientProvider = tableClientProvider;
            Instruments = instruments;
        }

        public ILogger<AzureTableJournalStorage> Logger { get; }

        public AzureTableJournalStorageOptions Options { get; }

        public string? JournalFormatKey { get; }

        public TableClientProvider TableClientProvider { get; }

        public AzureTableJournalStorageInstruments Instruments { get; }
    }

    internal abstract class TableClientProvider
    {
        public abstract TableClient GetTableClient();
    }

    internal sealed class InitializedTableClientProvider : TableClientProvider
    {
        private TableClient? _tableClient;

        public override TableClient GetTableClient()
            => _tableClient ?? throw new InvalidOperationException(
                $"{nameof(AzureTableJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

        public void SetTableClient(TableClient tableClient)
        {
            ArgumentNullException.ThrowIfNull(tableClient);
            _tableClient = tableClient;
        }
    }
}
