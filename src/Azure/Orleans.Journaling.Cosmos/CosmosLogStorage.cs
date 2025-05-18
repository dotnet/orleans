using Orleans.Serialization.Buffers;
using System.Net;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

internal sealed partial class CosmosLogStorage(
    string documentId, PartitionKey partitionKey,
    Container container, CosmosLogStorageOptions options,
    ILogger<CosmosLogStorage> logger) : IStateMachineStorage
{
    private readonly int _compactionThreshold = options.CompactionThreshold;
    private readonly QueryRequestOptions _requestOptions = new() { PartitionKey = partitionKey };

    private bool _isInitialized;
    private bool _isCompacted;
    private int _logEntriesCount;
    private long _nextSequenceNumber;
    private string? _compactedEntryETag;

    public bool IsCompactionRequested => !_isCompacted && _logEntriesCount > _compactionThreshold;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string CreateLogEntryId(long sequenceNumber) => $"{documentId}-{sequenceNumber}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string CreateCompactedEntryId() => $"{documentId}-compacted";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string CreatePendingCompactionEntryId() => $"{documentId}-pending-compaction";

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

        // Check for a pending compaction first.
        try
        {
            var pendingResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    CreatePendingCompactionEntryId(), partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (pendingResponse.StatusCode == HttpStatusCode.OK && pendingResponse.Resource is { } pendingResource)
            {
                LogPendingCompactionFound(logger, documentId);

                // A pending compaction exists. Attempt to complete it.
                await FinalizeCompactionAsync(pendingResource.Data, pendingResponse.ETag,
                    cancellationToken).ConfigureAwait(false);

                // State is now compacted!

                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;

                _compactedEntryETag = (await container.ReadItemAsync<CosmosLogEntry>(
                        CreateCompactedEntryId(), partitionKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).ETag;

                LogInitialized(logger, documentId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No pending compaction, proceed.
        }

        // Check for an existing compacted log.
        try
        {
            var compactedResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    CreateCompactedEntryId(), partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.OK)
            {
                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;
                _compactedEntryETag = compactedResponse.ETag;

                LogInitialized(logger, documentId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No compacted log, proceed to check for entries.
        }

        // Read existing log entries to determine current state
        List<CosmosLogEntry> entries = [];

        var query = new QueryDefinition(@"
                SELECT * FROM c
                WHERE c.LogId = @logId AND c.EntryType = @entryType
                ORDER BY c.SequenceNumber ASC")
            .WithParameter("@logId", documentId)
            .WithParameter("@entryType", CosmosLogEntryType.Default);

        using var feed = container.GetItemQueryIterator<CosmosLogEntry>(query, requestOptions: _requestOptions);
        long maxSequence = -1;

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
        _nextSequenceNumber = entries.Count > 0 ? maxSequence + 1 : 0;
        _compactedEntryETag = null;

        LogInitialized(logger, documentId, _isCompacted, _logEntriesCount);
    }

    public async ValueTask AppendAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        var data = builder.ToArray();
        CosmosLogEntry newEntry;

        if (_isCompacted)
        {
            // Current state is compacted, we need to uncompact it by means of:

            // 1) Reading the compacted entry.
            // 2) Deleting the compacted entry.
            // 3) Creating 2 new entries: one for old data, one for new entry.

            // All this should be done in a transactional batch to ensure atomicity.

            LogDecompacting(logger, documentId);

            var compactedResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    CreateCompactedEntryId(), partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.Resource is not { } compactedResource)
            {
                throw new InvalidOperationException($"Compacted item {documentId}-compacted not found during de-compaction.");
            }
                
            var batch = container.CreateTransactionalBatch(partitionKey);

            // We delete the old compacted entry
            batch.DeleteItem(CreateCompactedEntryId(), new TransactionalBatchItemRequestOptions { IfMatchEtag = _compactedEntryETag });

            var newCompactedEntry = new CosmosLogEntry
            {
                Id = CreateLogEntryId(0),
                LogId = documentId,
                SequenceNumber = 0,
                EntryType = CosmosLogEntryType.Default,
                Data = compactedResource.Data
            };

            batch.CreateItem(newCompactedEntry);

            newEntry = new CosmosLogEntry
            {
                Id = CreateLogEntryId(1),
                LogId = documentId,
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
                Id = CreateLogEntryId(_nextSequenceNumber),
                LogId = documentId,
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

        LogAppend(logger, builder.Length, documentId);
    }

    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_isCompacted)
        {
            var compactedResponse = await container.ReadItemAsync<CosmosLogEntry>(
                    CreateCompactedEntryId(), partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // This might happen if DeleteAsync was called and _initialized wasn't reset.
                // Or if initialization logic had a race. For safety, we yield break.
                LogWarnNotFoundOnRead(logger, documentId, CreateCompactedEntryId());

                yield break;
            }

            if (compactedResponse.Resource is not { } compactedResource)
            {
                yield break;
            }

            _compactedEntryETag = compactedResponse.ETag;

            LogRead(logger, compactedResource.Data.Length, documentId);

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
                .WithParameter("@logId", documentId)
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
                LogRead(logger, totalBytesRead, documentId);
            }
        }
    }

    public async ValueTask ReplaceAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        var data = builder.ToArray();

        var pendingEntry = new CosmosLogEntry // This is our "snapshot" marker.
        {
            Id = CreatePendingCompactionEntryId(),
            LogId = documentId,
            EntryType = CosmosLogEntryType.PendingCompaction,
            Data = data,
            SequenceNumber = 0
        };

        ItemResponse<CosmosLogEntry> pendingResponse;
        try
        {
            // Create if not exists, or replace if it exists (say from a failed previous attempt)
            pendingResponse = await container.UpsertItemAsync(pendingEntry,
                    partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            LogErrorCreatingPending(logger, documentId, ex.Message, ex);
            WrappedException.CreateAndRethrow(ex);

            throw;
        }

        await FinalizeCompactionAsync(data, pendingResponse.ETag, cancellationToken).ConfigureAwait(false);

        _isCompacted = true;
        _logEntriesCount = 1;
        _nextSequenceNumber = 0;

        _compactedEntryETag = (await container.ReadItemAsync<CosmosLogEntry>(
                CreateCompactedEntryId(), partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).ETag;

        LogReplaced(logger, documentId, data.Length);
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        // We need to delete all log entries (of any type) related to the logId.
        // This requires querying for all IDs first, then batch deleting.
        // Since LogId is the partition key, it should be efficient to do so.

        List<string> documentIds = [];

        // We use 'dynamic' for the query results because we are only interested in the document 'id',
        // making it a lightweight way to handle the minimal data returned.

        using var feed = container.GetItemQueryIterator<dynamic>(
            new QueryDefinition("SELECT c.id FROM c WHERE c.LogId = @logId").WithParameter("@logId", documentId),
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
            var batch = container.CreateTransactionalBatch(partitionKey);

            foreach (var itemId in documentIds)
            {
                batch.DeleteItem(itemId);
            }

            await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        _logEntriesCount = 0;
        _nextSequenceNumber = 0;
        _isCompacted = false;
        _compactedEntryETag = null;
        _isInitialized = false; // To force re-initialize on next the operation.

        LogDeleted(logger, documentId, documentIds.Count);
    }

    /// <summary>
    /// This finalizes the compaction which means it delete old entries,
    /// the pending compaction one, and creates a new compacted one.
    /// </summary>
    private async Task FinalizeCompactionAsync(byte[] compactedData, string pendingEntryETag, CancellationToken cancellationToken)
    {
        var batch = container.CreateTransactionalBatch(partitionKey);

        using var feed = container.GetItemQueryIterator<dynamic>(
            new QueryDefinition(@"
                SELECT c.id FROM c
                WHERE c.LogId = @logId AND (c.EntryType = @entry1 OR c.EntryType = @entry2)")
            .WithParameter("@logId", documentId)
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

        batch.DeleteItem(CreatePendingCompactionEntryId(), new TransactionalBatchItemRequestOptions { IfMatchEtag = pendingEntryETag });

        var newCompacted = new CosmosLogEntry
        {
            Id = CreateCompactedEntryId(),
            LogId = documentId,
            EntryType = CosmosLogEntryType.Compacted,
            Data = compactedData,
            SequenceNumber = 0
        };

        batch.CreateItem(newCompacted);

        var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!batchResponse.IsSuccessStatusCode)
        {
            LogErrorFinalizingCompaction(logger, documentId, batchResponse.StatusCode.ToString(), batchResponse.ErrorMessage);
            throw new InvalidOperationException($"Failed to finalize compaction for log {documentId}. Status: {batchResponse.StatusCode}. Error: {batchResponse.ErrorMessage}");
        }

        LogFinalizedCompaction(logger, documentId);
    }
}
