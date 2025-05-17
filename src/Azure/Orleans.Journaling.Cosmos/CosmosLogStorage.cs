using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Buffers;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

#pragma warning disable ORLEANSEXP005

internal sealed partial class CosmosLogStorage(
    string logId,
    CosmosLogStorageOptions options,
    IServiceProvider serviceProvider,
    ILogger<CosmosLogStorage> logger) : IStateMachineStorage
{
    [AllowNull] private CosmosClient _client;
    [AllowNull] private Container _container;

    private readonly PartitionKey _partitionKey = new(logId);
    private readonly int _compactionThreshold = options.CompactionThreshold;
    private readonly QueryRequestOptions _requestOptions = new() { PartitionKey = new PartitionKey(logId) };

    private bool _initialized;
    private bool _isCompacted;
    private int _logEntriesCount;
    private long _nextSequenceNumber;
    private string? _compactedEntryETag;

    public bool IsCompactionRequested => !_isCompacted && _logEntriesCount > _compactionThreshold;

    private string CompactedEntryId => $"{logId}-compacted";
    private string PendingCompactionEntryId => $"{logId}-pending-compaction";
    private string FormatEntryId(long sequenceNumber) => $"{logId}-{sequenceNumber}";

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
        CosmosLogEntry newEntry;

        if (_isCompacted)
        {
            // Current state is compacted, we need to uncompact it by means of:

            // 1) Reading the compacted entry.
            // 2) Deleting the compacted entry.
            // 3) Creating 2 new entries: one for old data, one for new entry.

            // All this should be done in a transactional batch to ensure atomicity.

            LogDecompacting(logger, logId);

            var compactedResponse = await _container.ReadItemAsync<CosmosLogEntry>(
                    CompactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.Resource is not { } compactedResource)
            {
                throw new InvalidOperationException($"Compacted item {logId}-compacted not found during de-compaction.");
            }
                
            var batch = _container.CreateTransactionalBatch(_partitionKey);

            // We delete the old compacted entry
            batch.DeleteItem(CompactedEntryId, new TransactionalBatchItemRequestOptions { IfMatchEtag = _compactedEntryETag });

            var newCompactedEntry = new CosmosLogEntry
            {
                Id = FormatEntryId(0),
                LogId = logId,
                SequenceNumber = 0,
                EntryType = CosmosLogEntryType.Default,
                Data = compactedResource.Data
            };

            batch.CreateItem(newCompactedEntry);

            newEntry = new CosmosLogEntry
            {
                Id = FormatEntryId(1),
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
                Id = FormatEntryId(_nextSequenceNumber),
                LogId = logId,
                SequenceNumber = _nextSequenceNumber,
                EntryType = CosmosLogEntryType.Default,
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
            var compactedResponse = await _container.ReadItemAsync<CosmosLogEntry>(
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

            yield return ToLogExtent(compactedResource.Data);
        }
        else
        {
            int itemsRead = 0;
            long totalBytesRead = 0;

            using var feed = _container.GetItemQueryIterator<CosmosLogEntry>(
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

        var pendingEntry = new CosmosLogEntry // This is our "snapshot" marker.
        {
            Id = PendingCompactionEntryId,
            LogId = logId,
            EntryType = CosmosLogEntryType.PendingCompaction,
            Data = data,
            SequenceNumber = 0
        };

        ItemResponse<CosmosLogEntry> pendingResponse;
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

        _compactedEntryETag = (await _container.ReadItemAsync<CosmosLogEntry>(
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

        // We use 'dynamic' for the query results because we are only interested in the document 'id',
        // making it a lightweight way to handle the minimal data returned.

        using var feed = _container.GetItemQueryIterator<dynamic>(
            new QueryDefinition("SELECT c.id FROM c WHERE c.LogId = @logId").WithParameter("@logId", logId),
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

        using var feed = _container.GetItemQueryIterator<dynamic>(
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

        batch.DeleteItem(PendingCompactionEntryId, new TransactionalBatchItemRequestOptions { IfMatchEtag = pendingEntryETag });

        var newCompacted = new CosmosLogEntry
        {
            Id = CompactedEntryId,
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
            throw new InvalidOperationException($"Failed to finalize compaction for log {logId}. Status: {batchResponse.StatusCode}. Error: {batchResponse.ErrorMessage}");
        }

        LogFinalizedCompaction(logger, logId);
    }
}
