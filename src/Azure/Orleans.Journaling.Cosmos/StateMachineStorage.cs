using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Buffers;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

#pragma warning disable ORLEANSEXP005

internal sealed partial class StateMachineStorage(
    string logId,
    LogStorageOptions options,
    IServiceProvider serviceProvider,
    ILogger<StateMachineStorage> logger) : IStateMachineStorage
{
    [AllowNull] private CosmosClient _client;
    [AllowNull] private Container _container;

    private readonly PartitionKey _partitionKey = new(logId);
    private readonly QueryRequestOptions _requestOptions = new() { PartitionKey = new PartitionKey(logId) };

    private const int CompactionThreshold = 10;

    private bool _initialized;
    private bool _isCompacted;
    private int _logEntriesCount;
    private long _nextSequenceNumber;
    private string? _compactedEntryETag;

    public bool IsCompactionRequested => !_isCompacted && _logEntriesCount > CompactionThreshold;

    private string CompactedEntryId => $"{logId}-compacted";
    private string PendingCompactionEntryId => $"{logId}-pending";

    private string FormatEntityId(long sequenceNumber) => $"{logId}-{sequenceNumber}";

    private static LogExtent ToLogExtent(byte[] data)
    {
        using var writer = new ArcBufferWriter();

        writer.Write(data);
        var buffer = writer.ConsumeSlice(writer.Length);

        return new(buffer);
    }

    public async ValueTask AppendAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var data = builder.ToArray();
        LogEntry newEntry;

        if (_isCompacted)
        {
            // Current state is compacted. We need to "un-compact" it by:
            // 1. Reading the compacted data.
            // 2. Deleting the compacted item.
            // 3. Creating two new log entries: one for old data, one for new.
            // All this should be done in a transactional batch.

            LogDecompacting(logger, logId);

            var compactedResponse = await _container.ReadItemAsync<LogEntry>(
                    CompactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.Resource is not { } compactedResource)
            {
                throw new InvalidOperationException($"Compacted item {logId}-compacted not found during de-compaction.");
            }
                
            var batch = _container.CreateTransactionalBatch(_partitionKey);

            // We delete the old compacted entry
            batch.DeleteItem(CompactedEntryId, new TransactionalBatchItemRequestOptions { IfMatchEtag = _compactedEntryETag });

            var newCompactedEntry = new LogEntry
            {
                Id = FormatEntityId(0),
                LogId = logId,
                SequenceNumber = 0,
                EntryType = LogEntryType.Default,
                Data = compactedResource.Data
            };

            batch.CreateItem(newCompactedEntry);

            newEntry = new LogEntry
            {
                Id = FormatEntityId(1),
                LogId = logId,
                SequenceNumber = 1,
                EntryType = LogEntryType.Default,
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
            newEntry = new LogEntry
            {
                Id = FormatEntityId(_nextSequenceNumber),
                LogId = logId,
                SequenceNumber = _nextSequenceNumber,
                EntryType = LogEntryType.Default,
                Data = data
            };

            await _container
                .CreateItemAsync(newEntry, _partitionKey, cancellationToken: cancellationToken)
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
            LogReadingCompacted(logger, logId);

            var compactedResponse = await _container.ReadItemAsync<LogEntry>(
                    CompactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // This might happen if DeleteAsync was called and _initialized wasn't reset.
                // Or if initialization logic had a race. For safety, we yield break.
                LogWarnNotFoundOnRead(logger, logId, CompactedEntryId);

                yield break;
            }

            if (compactedResponse.Resource is not { } compactedResource)
            {
                yield break;
            }

            _compactedEntryETag = compactedResponse.ETag;

            LogRead(logger, compactedResource.Data.Length, logId);

            yield return ToLogExtent(compactedResponse.Resource.Data);
        }
        else
        {
            LogReadingEntries(logger, logId, _logEntriesCount);

            var query = new QueryDefinition(@"
                    SELECT * FROM c
                    WHERE c.LogId = @logId AND c.EntryType = @entryType
                    ORDER BY c.SequenceNumber ASC")
                .WithParameter("@logId", logId)
                .WithParameter("@entryType", LogEntryType.Default);

            int itemsRead = 0;
            long totalBytesRead = 0;

            using var feed = _container.GetItemQueryIterator<LogEntry>(query, requestOptions: _requestOptions);

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

        var pendingEntry = new LogEntry // This is our "snapshot" marker.
        {
            Id = PendingCompactionEntryId,
            LogId = logId,
            EntryType = LogEntryType.PendingCompaction,
            Data = data,
            SequenceNumber = 0
        };

        ItemResponse<LogEntry> pendingResponse;
        try
        {
            // Create if not exists, or replace if it exists (say from a failed previous attempt)
            pendingResponse = await _container.UpsertItemAsync(pendingEntry,
                    _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            LogErrorCreatingPending(logger, logId, ex.Message, ex);
            WrappedException.CreateAndRethrow(ex);

            throw;
        }

        await FinalizeCompactionAsync(data, pendingResponse.ETag, cancellationToken).ConfigureAwait(false);

        _isCompacted = true;
        _logEntriesCount = 1;
        _nextSequenceNumber = 0;

        _compactedEntryETag = (await _container.ReadItemAsync<LogEntry>(
                CompactedEntryId, _partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).ETag;

        LogReplaced(logger, logId, data.Length);
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        // We need to delete all log entries (of any type) related to the logId.
        // This requires querying for all IDs first, then batch deleting.
        // Since LogId is the partition key, it should be efficient to do so.

        List<string> documentIds = [];

        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.LogId = @logId").WithParameter("@logId", logId);

        // We use 'dynamic' for the query results because we are only interested in the document 'id',
        // making it a lightweight way to handle the minimal data returned.

        using var feed = _container.GetItemQueryIterator<dynamic>(query, requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                documentIds.Add(item.id);
            }
        }

        if (documentIds.Count > 0)
        {
            var batch = _container.CreateTransactionalBatch(_partitionKey);

            foreach (var itemId in documentIds)
            {
                batch.DeleteItem(itemId);
            }

            await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        _initialized = false; // To force re-initialize on next the operation.
        _logEntriesCount = 0;
        _nextSequenceNumber = 0;
        _isCompacted = false;
        _compactedEntryETag = null;

        LogDeleted(logger, logId, documentIds.Count);
    }

    /// <summary>
    /// This finalizes the compaction which means it delete old entries,
    /// the pending compaction one, and creates a new compacted one.
    /// </summary>
    private async Task FinalizeCompactionAsync(byte[] compactedData, string pendingEntryETag, CancellationToken cancellationToken)
    {
        var batch = _container.CreateTransactionalBatch(_partitionKey);

        var queryOld = new QueryDefinition(@"
                SELECT c.id FROM c
                WHERE c.LogId = @logId AND (c.EntryType = @entry1 OR c.EntryType = @entry2)")
            .WithParameter("@logId", logId)
            .WithParameter("@entry1", LogEntryType.Default)
            .WithParameter("@entry2", LogEntryType.Compacted);

        using var feed = _container.GetItemQueryIterator<dynamic>(queryOld, requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            foreach (var item in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.DeleteItem(item.id);
            }
        }

        batch.DeleteItem(PendingCompactionEntryId, new TransactionalBatchItemRequestOptions { IfMatchEtag = pendingEntryETag });

        var newCompacted = new LogEntry
        {
            Id = CompactedEntryId,
            LogId = logId,
            EntryType = LogEntryType.Compacted,
            Data = compactedData,
            SequenceNumber = 0
        };

        batch.CreateItem(newCompacted);

        var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!batchResponse.IsSuccessStatusCode)
        {
            LogErrorFinalizingCompaction(logger, logId, batchResponse.StatusCode.ToString(), batchResponse.ErrorMessage);
            throw new InvalidOperationException($"Failed to finalize compaction for log {logId}. Status: {batchResponse.StatusCode}. Error: {batchResponse.ErrorMessage}");
        }

        LogFinalizedCompaction(logger, logId);
    }
}
