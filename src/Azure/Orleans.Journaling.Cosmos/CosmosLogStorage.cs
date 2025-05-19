using Microsoft.CodeAnalysis;
using Orleans.Serialization.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling.Cosmos;

internal sealed partial class CosmosLogStorage(
    string logId, PartitionKey partitionKey,
    Container container, CosmosLogStorageOptions options,
    ILogger<CosmosLogStorage> logger) : IStateMachineStorage
{
    private readonly string _compactedEntryId = $"{logId}-compacted";
    private readonly string _compactionPendingEntryId = $"{logId}-compaction-pending";
    private readonly int _compactionThreshold = options.CompactionThreshold;
    private readonly QueryRequestOptions _requestOptions = new() { PartitionKey = partitionKey };

    private bool _isInitialized;
    private bool _isCompacted;
    private int _logEntriesCount;
    private long _nextSequenceNumber;
    private string? _compactedEntryETag;

    public bool IsCompactionRequested
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_isCompacted && _logEntriesCount > _compactionThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string CreateEntryId(long sequenceNumber) => $"{logId}-{sequenceNumber}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LogExtent ToLogExtent(byte[] data)
    {
        using var writer = new ArcBufferWriter();

        writer.Write(data);
        var buffer = writer.ConsumeSlice(writer.Length);

        return new(buffer);
    }

    public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return;
        }

        // We first check for a pending compaction entry.
        try
        {
            var pendingResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    _compactionPendingEntryId, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (pendingResponse.StatusCode == HttpStatusCode.OK && pendingResponse.Resource is { } pendingResource)
            {
                LogPendingCompactionFound(logger, logId);

                // A pending compaction exists, so we attempt to complete it.
                await FinalizeCompactionAsync(pendingResource.Data, pendingResponse.ETag,
                    cancellationToken).ConfigureAwait(false);

                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;

                _compactedEntryETag = (await container.ReadItemAsync<CosmosLogEntry>(
                        _compactedEntryId, partitionKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).ETag;

                LogInitialized(logger, logId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No compaction pending entry.
        }

        // Next we check for an existing compacted entry.
        try
        {
            var compactedResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    _compactedEntryId, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.OK)
            {
                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;
                _compactedEntryETag = compactedResponse.ETag;

                LogInitialized(logger, logId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No compacted entry.
        }

        // Lastly we read existing entries to determine current state.

        List<CosmosLogEntry> entries = [];

        var query = new QueryDefinition(@"
                SELECT * FROM c
                WHERE c.LogId = @logId AND c.EntryType = @entryType
                ORDER BY c.SequenceNumber ASC")
            .WithParameter("@logId", logId)
            .WithParameter("@entryType", CosmosLogEntryType.Default);

        long maxSequence = -1;
        using var feed = container.GetItemQueryIterator<CosmosLogEntry>(query, requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(item);

                if (item.SequenceNumber > maxSequence)
                {
                    maxSequence = item.SequenceNumber;
                }
            }
        }

        _isInitialized = true;
        _isCompacted = false;
        _logEntriesCount = entries.Count;
        _nextSequenceNumber = maxSequence + 1;
        _compactedEntryETag = null;

        LogInitialized(logger, logId, _isCompacted, _logEntriesCount);
    }

    public async ValueTask AppendAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var data = builder.ToArray();

        CosmosLogEntry newEntry;

        if (_isCompacted)
        {
            Debug.Assert(_compactedEntryETag != null);

            // Current state is compacted, we need to uncompact it by means of:

            // 1) Reading the compacted entry.
            // 2) Deleting the compacted entry.
            // 3) Creating 2 new entries: one for old data, one for new entry.

            // All this should be done in a transactional batch to ensure atomicity.

            LogDecompacting(logger, logId);

            var compactedResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    _compactedEntryId, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.Resource is not { } compactedResource)
            {
                throw new InvalidOperationException($"Compacted item {logId}-compacted not found during de-compaction.");
            }
                
            var batch = container.CreateTransactionalBatch(partitionKey);

            // We delete the old compacted entry
            batch.DeleteItem(_compactedEntryId, new TransactionalBatchItemRequestOptions { IfMatchEtag = _compactedEntryETag });

            var newCompactedEntry = new CosmosLogEntry
            {
                Id = CreateEntryId(0),
                LogId = logId,
                SequenceNumber = 0,
                EntryType = CosmosLogEntryType.Default,
                Data = compactedResource.Data
            };

            batch.CreateItem(newCompactedEntry);

            newEntry = new CosmosLogEntry
            {
                Id = CreateEntryId(1),
                LogId = logId,
                SequenceNumber = 1,
                EntryType = CosmosLogEntryType.Default,
                Data = data
            };

            batch.CreateItem(newEntry);

            var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (!batchResponse.IsSuccessStatusCode)
            {
                throw new CosmosException($"Failed to de-compact and append. Status: {batchResponse.StatusCode}, " +
                    $"Error: {batchResponse.ErrorMessage}", batchResponse.StatusCode, 0,
                    batchResponse.ActivityId, batchResponse.RequestCharge);
            }

            _isCompacted = false;
            _compactedEntryETag = null;
            _logEntriesCount = 2;
            _nextSequenceNumber = 2;
        }
        else
        {
            newEntry = new CosmosLogEntry
            {
                Id = CreateEntryId(_nextSequenceNumber),
                LogId = logId,
                SequenceNumber = _nextSequenceNumber,
                EntryType = CosmosLogEntryType.Default,
                Data = data
            };

            await container
                .CreateItemAsync(newEntry, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logEntriesCount++;
            _nextSequenceNumber++;
        }

        LogAppend(logger, builder.Length, logId);
    }

    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (_isCompacted)
        {
            var compactedResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    _compactedEntryId, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // This might happen if DeleteAsync was called and _initialized wasn't reset.
                // Or if initialization logic had a race. For safety, we yield break.
                LogWarnNotFoundOnRead(logger, logId, _compactedEntryId);

                yield break;
            }

            if (compactedResponse.Resource is not { } compactedResource)
            {
                yield break;
            }

            _compactedEntryETag = compactedResponse.ETag;

            LogRead(logger, compactedResource.Data.Length, logId);

            yield return ToLogExtent(compactedResource.Data);
        }
        else
        {
            int itemsRead = 0;
            long totalBytesRead = 0;

            using var feed = container.GetItemQueryIterator<CosmosLogEntry>(
                new QueryDefinition(@"
                    SELECT * FROM c
                    WHERE c.LogId = @logId AND c.EntryType = @entryType
                    ORDER BY c.SequenceNumber ASC")
                        .WithParameter("@logId", logId)
                        .WithParameter("@entryType", CosmosLogEntryType.Default),
                    requestOptions: _requestOptions);

            while (feed.HasMoreResults)
            {
                foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    itemsRead++;
                    totalBytesRead += item.Data.Length;

                    yield return ToLogExtent(item.Data);
                }
            }

            if (itemsRead > 0)
            {
                LogRead(logger, totalBytesRead, logId);
            }
        }
    }

    public async ValueTask ReplaceAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var data = builder.ToArray();
        var pendingEntry = new CosmosLogEntry
        {
            Id = _compactionPendingEntryId,
            LogId = logId,
            EntryType = CosmosLogEntryType.CompactionPending,
            Data = data,
            SequenceNumber = 0
        };

        // Create if not exists, or replace if it exists (say from a previous failed attempt)
       var pendingResponse = await container.UpsertItemAsync(
                pendingEntry, partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await FinalizeCompactionAsync(data, pendingResponse.ETag, cancellationToken).ConfigureAwait(false);

        _isCompacted = true;
        _logEntriesCount = 1;
        _nextSequenceNumber = 0;

        _compactedEntryETag = (await container.ReadItemAsync<CosmosLogEntry>(
                _compactedEntryId, partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).ETag;

        LogReplaced(logger, logId, data.Length);
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // We need to delete all log entries (of any type) related to the logId.
        // This requires querying for all IDs first, then batch deleting.
        // Since LogId is the partition key, it should be efficient to do so.

        List<string> documentIds = [];

        // We use 'dynamic' for the query results because we are only interested in the document 'id',
        // making it a lightweight way to handle the minimal data returned.

        using var feed = container.GetItemQueryIterator<dynamic>(
            new QueryDefinition("SELECT c.id FROM c WHERE c.LogId = @logId")
                .WithParameter("@logId", logId),
            requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                documentIds.Add(item.id);
            }
        }

        if (documentIds.Count > 0)
        {
            const int BatchSize = 100; // Transactional batch has a limit of 100 operations

            for (var i = 0; i < documentIds.Count; i += BatchSize)
            {
                var batch = container.CreateTransactionalBatch(partitionKey);
                var batchIds = documentIds.Skip(i).Take(BatchSize);

                foreach (var itemId in batchIds)
                {
                    batch.DeleteItem(itemId);
                }

                await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _logEntriesCount = 0;
        _nextSequenceNumber = 0;
        _isCompacted = false;
        _compactedEntryETag = null;
        _isInitialized = false; // To force re-initialize on next the operation.

        LogDeleted(logger, logId, documentIds.Count);
    }

    /// <summary>
    /// This finalizes the compaction. It first attempts to delete old log entries in batches,
    /// then executes a critical transaction to delete the pending compaction entry
    /// and create the new compacted entry.
    /// </summary>
    private async Task FinalizeCompactionAsync(byte[] compactedData, string pendingEntryETag, CancellationToken cancellationToken)
    {
        var batch = container.CreateTransactionalBatch(partitionKey);

        using var feed = container.GetItemQueryIterator<dynamic>(
            new QueryDefinition(@"
                SELECT c.id FROM c
                WHERE c.LogId = @logId AND (c.EntryType = @entry1 OR c.EntryType = @entry2)")
            .WithParameter("@logId", logId)
            .WithParameter("@entry1", CosmosLogEntryType.Default)
            .WithParameter("@entry2", CosmosLogEntryType.Compacted),
                requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.DeleteItem(item.id);
            }
        }

        batch.DeleteItem(_compactionPendingEntryId, new
            TransactionalBatchItemRequestOptions { IfMatchEtag = pendingEntryETag });

        var newCompacted = new CosmosLogEntry
        {
            Id = _compactedEntryId,
            LogId = logId,
            EntryType = CosmosLogEntryType.Compacted,
            Data = compactedData,
            SequenceNumber = 0
        };

        batch.CreateItem(newCompacted);

        var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!batchResponse.IsSuccessStatusCode)
        {
            LogErrorFinalizingCompaction(logger, logId, batchResponse.StatusCode.ToString(), batchResponse.ErrorMessage);
            throw new InvalidOperationException($"Failed to finalize compaction for log {logId}. " +
                $"Status: {batchResponse.StatusCode}. Error: {batchResponse.ErrorMessage}");
        }

        LogFinalizedCompaction(logger, logId);
    }
}
