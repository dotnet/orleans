using Orleans.Serialization.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using static Orleans.Journaling.Cosmos.CosmosIdSanitizer;

namespace Orleans.Journaling.Cosmos;

//TODO: Use that cosmos executor interface
//TODO: Check ETag usages
//TODO: Add pre-post logging

internal sealed partial class CosmosLogStorage : IStateMachineStorage
{
    private bool _isInitialized;
    private bool _isCompacted;
    private int _logEntriesCount;
    private long _nextSequenceNumber;
    private string? _compactedEntryETag;

    private readonly string _logId;
    private readonly PartitionKey _partitionKey;
    private readonly int _compactionThreshold;
    private readonly string _compactedEntryId;
    private readonly string _compactionPendingEntryId;
    private readonly Container _container;
    private readonly QueryRequestOptions _requestOptions;
    private readonly ILogger<CosmosLogStorage> _logger;

    public CosmosLogStorage(
        GrainId grainId, string serviceId, Container container,
        CosmosLogStorageOptions options, ILogger<CosmosLogStorage> logger)
    {
        _logId = $"{Sanitize(serviceId)}{SeparatorChar}{Sanitize(grainId.Type.ToString()!)}" +
                 $"{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

        _partitionKey = new PartitionKey(_logId);

        _compactionThreshold = options.CompactionThreshold;
        _compactedEntryId = $"{_logId}{SeparatorChar}compacted";
        _compactionPendingEntryId = $"{_logId}{SeparatorChar}compaction{SeparatorChar}pending";

        _requestOptions = new() { PartitionKey = _partitionKey };
        _container = container;
        _logger = logger;
    }

    public bool IsCompactionRequested
    {
        get
        {
            if (_logEntriesCount > _compactionThreshold)
            {
                Debug.Assert(!_isCompacted);
                return true;
            }

            return false;
        }
    }

    private string CreateEntryId(long sequenceNumber) => $"{_logId}{SeparatorChar}{sequenceNumber}";

    public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return;
        }

        // We first check for a pending compaction entry.
        try
        {
            var pendingResponse = await _container.ReadItemAsync<LogEntry>(
                    _compactionPendingEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (pendingResponse.StatusCode == HttpStatusCode.OK && pendingResponse.Resource is { } pendingResource)
            {
                LogPendingCompactionFound(_logger, _logId);

                // A pending compaction exists, so we attempt to complete it.
                await FinalizeCompactionAsync(pendingResource.Data, pendingResponse.ETag,
                    cancellationToken).ConfigureAwait(false);

                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;

                _compactedEntryETag = (await _container.ReadItemAsync<LogEntry>(
                        _compactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).ETag;

                LogInitialized(_logger, _logId, _isCompacted, _logEntriesCount);

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
            var compactedResponse = await _container.ReadItemAsync<LogEntry>(
                    _compactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.OK)
            {
                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;
                _compactedEntryETag = compactedResponse.ETag;

                LogInitialized(_logger, _logId, _isCompacted, _logEntriesCount);

                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No compacted entry.
        }

        // Lastly we read existing entries to determine current state.

        List<LogEntry> entries = [];

        var query = new QueryDefinition(@"
                SELECT * FROM c
                WHERE c.LogId = @logId AND c.EntryType = @entryType
                ORDER BY c.SequenceNumber ASC")
            .WithParameter("@logId", _logId)
            .WithParameter("@entryType", LogEntryType.Log);

        long maxSequence = -1;
        using var feed = _container.GetItemQueryIterator<LogEntry>(query, requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

        LogInitialized(_logger, _logId, _isCompacted, _logEntriesCount);
    }

    public async ValueTask AppendAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var data = builder.ToArray();

        LogEntry newEntry;

        if (_isCompacted)
        {
            Debug.Assert(_compactedEntryETag != null);

            // Current state is compacted, we need to uncompact it by means of:

            // 1) Reading the compacted entry.
            // 2) Deleting the compacted entry.
            // 3) Creating 2 new entries: one for old data, one for new entry.

            // All this should be done in a transactional batch to ensure atomicity.

            LogDecompacting(_logger, _logId);

            var compactedResponse = await _container.ReadItemAsync<LogEntry>(
                    _compactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.Resource is not { } compactedResource)
            {
                throw new InvalidOperationException($"Compacted item {_logId}-compacted not found during de-compaction.");
            }
                
            var batch = _container.CreateTransactionalBatch(_partitionKey);

            // We delete the old compacted entry
            batch.DeleteItem(_compactedEntryId, new TransactionalBatchItemRequestOptions { IfMatchEtag = _compactedEntryETag });

            var newCompactedEntry = new LogEntry
            {
                Id = CreateEntryId(0),
                LogId = _logId,
                SequenceNumber = 0,
                EntryType = LogEntryType.Log,
                Data = compactedResource.Data
            };

            batch.CreateItem(newCompactedEntry);

            newEntry = new LogEntry
            {
                Id = CreateEntryId(1),
                LogId = _logId,
                SequenceNumber = 1,
                EntryType = LogEntryType.Log,
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
                Id = CreateEntryId(_nextSequenceNumber),
                LogId = _logId,
                SequenceNumber = _nextSequenceNumber,
                EntryType = LogEntryType.Log,
                Data = data
            };

            await _container
                .CreateItemAsync(newEntry, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logEntriesCount++;
            _nextSequenceNumber++;
        }

        LogAppend(_logger, builder.Length, _logId);
    }

    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (_isCompacted)
        {
            var compactedResponse = await _container.ReadItemAsync<LogEntry>(
                    _compactedEntryId, _partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // This might happen if DeleteAsync was called and _initialized wasn't reset.
                // Or if initialization logic had a race. For safety, we yield break.
                LogWarnNotFoundOnRead(_logger, _logId, _compactedEntryId);

                yield break;
            }

            if (compactedResponse.Resource is not { } compactedResource)
            {
                yield break;
            }

            _compactedEntryETag = compactedResponse.ETag;

            using var writer = new ArcBufferWriter();
            writer.Write(compactedResource.Data);

            LogRead(_logger, compactedResource.Data.Length, _logId);

            yield return new LogExtent(writer.ConsumeSlice(writer.Length));
        }
        else
        {
            using var feed = _container.GetItemQueryIterator<LogEntry>(
                new QueryDefinition(@"
                    SELECT * FROM c
                    WHERE c.LogId = @logId AND c.EntryType = @entryType
                    ORDER BY c.SequenceNumber ASC")
                        .WithParameter("@logId", _logId)
                        .WithParameter("@entryType", LogEntryType.Log),
                    requestOptions: _requestOptions);

            long bytesRead = 0;
            using var writer = new ArcBufferWriter();

            while (feed.HasMoreResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var entry in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    var data = entry.Data;

                    writer.Write(data); // No need to advance on the writer, as writing does so internally.
                    bytesRead += data.Length;
                }
            }

            // After reading all entries and accumulating them in buffer, we yield one single LogExtent
            // for the entire accumulated content. This is purely done so to be in accordance with
            // the AzureAppendBlob implemation.

            if (writer.Length > 0)
            {
                LogRead(_logger, bytesRead, _logId);
                yield return new LogExtent(writer.ConsumeSlice(writer.Length));
            }
            else
            {
                LogRead(_logger, 0, _logId);
                yield break;
            }
        }
    }

    public async ValueTask ReplaceAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var data = builder.ToArray();
        var pendingEntry = new LogEntry
        {
            Id = _compactionPendingEntryId,
            LogId = _logId,
            EntryType = LogEntryType.CompactionPending,
            Data = data,
            SequenceNumber = 0
        };

        // Create if not exists, or replace if it exists (say from a previous failed attempt)
       var pendingResponse = await _container.UpsertItemAsync(
                pendingEntry, _partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await FinalizeCompactionAsync(data, pendingResponse.ETag, cancellationToken).ConfigureAwait(false);

        _isCompacted = true;
        _logEntriesCount = 1;
        _nextSequenceNumber = 0;

        _compactedEntryETag = (await _container.ReadItemAsync<LogEntry>(
                _compactedEntryId, _partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).ETag;

        LogReplaced(_logger, _logId, data.Length);
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

        using var feed = _container.GetItemQueryIterator<LogEntryId>(
            new QueryDefinition("SELECT c.id FROM c WHERE c.LogId = @logId")
                .WithParameter("@logId", _logId),
            requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var entryId in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                documentIds.Add(entryId.Value);
            }
        }

        if (documentIds.Count > 0)
        {
            const int BatchSize = 100; // Transactional batch has a limit of 100 operations

            for (var i = 0; i < documentIds.Count; i += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = _container.CreateTransactionalBatch(_partitionKey);
                var batchIds = documentIds.Skip(i).Take(BatchSize);

                foreach (var docId in batchIds)
                {
                    batch.DeleteItem(docId);
                }

                await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _logEntriesCount = 0;
        _nextSequenceNumber = 0;
        _isCompacted = false;
        _compactedEntryETag = null;
        _isInitialized = false; // To force re-initialize on next the operation.

        LogDeleted(_logger, _logId, documentIds.Count);
    }

    /// <summary>
    /// This finalizes the compaction. It first attempts to delete old log entries in batches,
    /// then executes a critical transaction to delete the pending compaction entry
    /// and create the new compacted entry.
    /// </summary>
    private async Task FinalizeCompactionAsync(byte[] compactedData, string pendingEntryETag, CancellationToken cancellationToken)
    {
        var batch = _container.CreateTransactionalBatch(_partitionKey);

        using var feed = _container.GetItemQueryIterator<LogEntryId>(
            new QueryDefinition(@"
                SELECT c.id FROM c
                WHERE c.LogId = @logId AND (c.EntryType = @entry1 OR c.EntryType = @entry2)")
            .WithParameter("@logId", _logId)
            .WithParameter("@entry1", LogEntryType.Log)
            .WithParameter("@entry2", LogEntryType.Compacted),
                requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var entryId in await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.DeleteItem(entryId.Value);
            }
        }

        batch.DeleteItem(_compactionPendingEntryId, new() { IfMatchEtag = pendingEntryETag });

        var newCompacted = new LogEntry
        {
            Id = _compactedEntryId,
            LogId = _logId,
            EntryType = LogEntryType.Compacted,
            Data = compactedData,
            SequenceNumber = 0
        };

        batch.CreateItem(newCompacted);

        var batchResponse = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!batchResponse.IsSuccessStatusCode)
        {
            LogErrorFinalizingCompaction(_logger, _logId, batchResponse.StatusCode.ToString(), batchResponse.ErrorMessage);
            throw new InvalidOperationException($"Failed to finalize compaction for log {_logId}. " +
                $"Status: {batchResponse.StatusCode}. Error: {batchResponse.ErrorMessage}");
        }

        LogFinalizedCompaction(_logger, _logId);
    }
}
