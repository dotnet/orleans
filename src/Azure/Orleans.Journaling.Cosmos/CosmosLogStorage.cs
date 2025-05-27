using Orleans.Serialization.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using static Orleans.Journaling.Cosmos.CosmosIdSanitizer;

namespace Orleans.Journaling.Cosmos;

internal sealed partial class CosmosLogStorage : IStateMachineStorage
{
    // These fields are the in-memory representation of the log state.
    // We could encapsulate this into a class and handle the transitions there,
    // but considering that a 'CosmosLogStorage' instance is created for every grain activation
    // we save a good amount of memory by object flattening that state as fields here.

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
    private readonly ICosmosOperationExecutor _executor;

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

    public CosmosLogStorage(
        GrainId grainId, string serviceId, Container container,
        CosmosLogStorageOptions options, ILogger<CosmosLogStorage> logger)
    {
        _logId = $"{Sanitize(serviceId)}{SeparatorChar}{Sanitize(grainId.Type.ToString()!)}" +
                 $"{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

        // We do not use something like the IPartitionKeyProvider, because contrary to the grain
        // storage provider, where people might be querying the cosmos documents directly and skip going
        // through the grain for reads. It is reasonable to do a cross-grain query in cosmos so a partition key,
        // scoped to the grain type might be used. In the case of log-based storage (this case), the data is
        // binary so it makes very little sense to query that outside the grain, as decoding is neccessary, which leaks
        // the protocol of the LogExtend. Knowing this, we can optimize for querying only the documents that
        // pertain to this log instance, therefor we use this fixed partition key.

        _partitionKey = new PartitionKey(_logId);

        _compactionThreshold = options.CompactionThreshold;
        _compactedEntryId = $"{_logId}{SeparatorChar}compacted";
        _compactionPendingEntryId = $"{_logId}{SeparatorChar}compaction{SeparatorChar}pending";

        _requestOptions = new() { PartitionKey = _partitionKey };
        _container = container;

        _logger = logger;
        _executor = options.OperationExecutor;
    }

    public async ValueTask AppendAsync(LogExtentBuilder builder, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var data = builder.ToArray();

        CosmosLogEntry newEntry;

        if (!_isCompacted)
        {
            newEntry = new CosmosLogEntry
            {
                Id = CreateEntryId(_nextSequenceNumber),
                LogId = _logId,
                SequenceNumber = _nextSequenceNumber,
                EntryType = LogEntryType.Log,
                Data = data
            };

            _ = await _executor.ExecuteOperation(static args =>
            {
                var (self, entry, pk, ct) = args;
                return self._container.CreateItemAsync(entry, pk, cancellationToken: ct);
            }, (this, newEntry, _partitionKey, cancellationToken)).ConfigureAwait(false);

            _logEntriesCount++;
            _nextSequenceNumber++;

            LogAppend(_logger, builder.Length, _logId);
            return;
        }

        Debug.Assert(_logEntriesCount == 1);        // There must be 1 entry (the compacted one)
        Debug.Assert(_nextSequenceNumber == 0);
        Debug.Assert(_compactedEntryETag != null);

        // When AppendAsync is called and the log is currently compacted, it means there is a single document
        // in Cosmos (having id = _compactedEntryId) holding all the previously appended data.
        // To append new data, we need to:

        // 1) Read the content of the current compacted entry.
        // 2) Treat this content as the first log entry (seqnum = 0) of a new (uncompacted) series.
        // 3) Append the new data as the second log entry (seqnum = 1).
        // 4) Delete the old compacted entry.
        // 5) Update the in-memory state.

        // All this (delete old, create two new) should be done in a transactional batch to ensure atomicity.

        LogDecompacting(_logger, _logId);

        var compactedResponse = await _executor.ExecuteOperation(static args =>
        {
            var (self, id, pk, ct) = args;
            return self._container.ReadItemAsync<CosmosLogEntry>(id, pk, cancellationToken: ct);
        },
        (this, _compactedEntryId, _partitionKey, cancellationToken)).ConfigureAwait(false);

        if (compactedResponse.Resource is not { } compactedResource)
        {
            throw new InvalidOperationException($"Compacted log entry = {_compactedEntryId}, was not found during de-compaction.");
        }
                
        var batch = _container.CreateTransactionalBatch(_partitionKey);

        // We delete the old compacted entry
        batch.DeleteItem(_compactedEntryId, new() { IfMatchEtag = _compactedEntryETag });

        var snapshotEntry = new CosmosLogEntry
        {
            Id = CreateEntryId(0),
            LogId = _logId,
            SequenceNumber = 0,
            EntryType = LogEntryType.Log, // We intentionally do NOT set the type to 'Compacted' here.
            Data = compactedResource.Data // All compacted data now becomes part of the snapshot.
        };

        batch.CreateItem(snapshotEntry);

        newEntry = new CosmosLogEntry
        {
            Id = CreateEntryId(1),
            LogId = _logId,
            SequenceNumber = 1,
            EntryType = LogEntryType.Log,  // Again type 'Log', this is the new entry being provided by the SM manager.
            Data = data
        };

        batch.CreateItem(newEntry);

        var batchResponse = await _executor.ExecuteOperation(static args =>
        {
            var (batch, ct) = args;
            return batch.ExecuteAsync(ct);
        },
        (batch, cancellationToken)).ConfigureAwait(false);

        if (!batchResponse.IsSuccessStatusCode)
        {
            throw new CosmosException($"Failed to de-compact and append. Status: {batchResponse.StatusCode}, " +
                $"Error: {batchResponse.ErrorMessage}", batchResponse.StatusCode, 0,
                batchResponse.ActivityId, batchResponse.RequestCharge);
        }

        _isCompacted = false;
        _compactedEntryETag = null;
        _logEntriesCount = 2; // 2 because one for the compacted entry, and another for the appended.
        _nextSequenceNumber = 2; // 2 because 0 was used for the compacted entry, and 1 for the appended, next will be 2.
   
        LogAppend(_logger, builder.Length, _logId);
    }

    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (_isCompacted)
        {
            var compactedResponse = await _executor.ExecuteOperation(static args =>
            {
                var (self, id, pk, ct) = args;
                return self._container.ReadItemAsync<CosmosLogEntry>(id, pk, cancellationToken: ct);
            },
            (this, _compactedEntryId, _partitionKey, cancellationToken)).ConfigureAwait(false);

            if (compactedResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // This might happen if DeleteAsync was called and _isInitialized was not reset.
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
            using var feed = _container.GetItemQueryIterator<CosmosLogEntry>(
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
                var feedResponse = await _executor.ExecuteOperation(static args =>
                {
                    var (feed, ct) = args;
                    return feed.ReadNextAsync(ct);
                },
                (feed, cancellationToken)).ConfigureAwait(false);

                foreach (var entry in feedResponse)
                {
                    var data = entry.Data;

                    writer.Write(data); // No need to advance on the writer, as writing does so internally.
                    bytesRead += data.Length;
                }
            }

            // After reading all entries and accumulating them in buffer, we yield one single LogExtent
            // for the entire accumulated content. This is purely done so to be in accordance with
            // the AzureAppendBlob implementation.

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
        var pendingEntry = new CosmosLogEntry
        {
            Id = _compactionPendingEntryId,
            LogId = _logId,
            EntryType = LogEntryType.CompactionPending,
            Data = data,
            SequenceNumber = 0
        };

        // The pending compaction entry is used to make the compaction process more resilient to failures, it is like a safe marker.
        // We create if not exists, or replace if it exists (say from a previous failed attempt).

        var pendingResponse = await _executor.ExecuteOperation(static args =>
        {
            var (self, entry, pk, ct) = args;
            return self._container.UpsertItemAsync(entry, pk, cancellationToken: ct);
        },
        (this, pendingEntry, _partitionKey, cancellationToken)).ConfigureAwait(false);

        await CompactAsync(data, pendingResponse.ETag, cancellationToken).ConfigureAwait(false);

        _isCompacted = true;
        _logEntriesCount = 1;
        _nextSequenceNumber = 0;

        var compactedResponse = await _executor.ExecuteOperation(static args =>
        {
            var (self, id, pk, ct) = args;
            return self._container.ReadItemAsync<CosmosLogEntry>(id, pk, cancellationToken: ct);
        },
        (this, _compactedEntryId, _partitionKey, cancellationToken)).ConfigureAwait(false);

        _compactedEntryETag = compactedResponse.ETag;

        LogReplaced(_logger, _logId, data.Length);
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // We need to delete all log entries (of any type) related to the logId.
        // This requires querying for all IDs first, then batch deleting.
        // Since LogId is the partition key, it should be efficient to do so.
        // We use 'LogEntryId' for the query results because we are only interested in the document 'id',
        // making the returned data size as minimal as possible.

        var batch = _container.CreateTransactionalBatch(_partitionKey);
        var entryCount = 0;

        using var feed = _container.GetItemQueryIterator<LogEntryId>(
            new QueryDefinition("SELECT c.id FROM c WHERE c.LogId = @logId")
                .WithParameter("@logId", _logId),
            requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            var feedResponse = await _executor.ExecuteOperation(static args =>
            {
                var (feed, ct) = args;
                return feed.ReadNextAsync(ct);
            },
            (feed, cancellationToken)).ConfigureAwait(false);

            foreach (var entryId in feedResponse)
            {
                batch.DeleteItem(entryId.Value);
                entryCount++;
            }
        }

        var batchResponse = await _executor.ExecuteOperation(static args =>
        {
            var (batch, ct) = args;
            return batch.ExecuteAsync(ct);
        },
        (batch, cancellationToken)).ConfigureAwait(false);

        if (!batchResponse.IsSuccessStatusCode)
        {
            _isInitialized = false; // We do this so when a recovery is initiated we are sure to work with the most recent state.

            LogErrorDeleting(_logger, _logId, batchResponse.StatusCode.ToString(), batchResponse.ErrorMessage);

            throw new CosmosException($"Failed to delete the entries for log {_logId}. " +
                $"Status: {batchResponse.StatusCode}, Error: {batchResponse.ErrorMessage}",
                batchResponse.StatusCode, 0, batchResponse.ActivityId, batchResponse.RequestCharge);
        }

        _logEntriesCount = 0;
        _nextSequenceNumber = 0;
        _isCompacted = false;
        _compactedEntryETag = null;

        LogDeleted(_logger, _logId, entryCount);
    }

    /// <summary>
    /// Creates a unique document id for <see cref="CosmosLogEntry"/>.
    /// </summary>
    private string CreateEntryId(long sequenceNumber) => $"{_logId}{SeparatorChar}{sequenceNumber}";

    /// <summary>
    /// All of the <see cref="IStateMachineStorage"/> operations depend on knowing the current state of
    /// the log. Is it compacted? what is the next sequence number? what log entries exist etc. So this
    /// must be called before each operation, but we do it lazily (and only once) upon operation invocation.
    /// </summary>
    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return;
        }

        // We first check for a pending compaction entry.
        try
        {
            var pendingResponse = await _executor.ExecuteOperation(static args =>
            {
                var (self, id, pk, ct) = args;
                return self._container.ReadItemAsync<CosmosLogEntry>(id, pk, cancellationToken: ct);
            },
            (this, _compactionPendingEntryId, _partitionKey, cancellationToken)).ConfigureAwait(false);

            if (pendingResponse.StatusCode == HttpStatusCode.OK && pendingResponse.Resource is { } pendingResource)
            {
                LogPendingCompactionFound(_logger, _logId);

                // A pending compaction exists, so we attempt to complete it.
                await CompactAsync(pendingResource.Data, pendingResponse.ETag,
                    cancellationToken).ConfigureAwait(false);

                _isInitialized = true;
                _isCompacted = true;
                _logEntriesCount = 1;
                _nextSequenceNumber = 0;
                _compactedEntryETag = null;

                var compactedResponse = await _executor.ExecuteOperation(static args =>
                {
                    var (self, id, pk, ct) = args;
                    return self._container.ReadItemAsync<CosmosLogEntry>(id, pk, cancellationToken: ct);
                },
                (this, _compactedEntryId, _partitionKey, cancellationToken)).ConfigureAwait(false);

                _compactedEntryETag = compactedResponse.ETag;

                LogInitialized(_logger, _logId, _isCompacted, _logEntriesCount);
                return;
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Ignore, no pending compaction.
        }

        // Next we check for an existing compacted entry.
        try
        {
            var compactedResponse = await _executor.ExecuteOperation(static args =>
            {
                var (self, id, pk, ct) = args;
                return self._container.ReadItemAsync<CosmosLogEntry>(id, pk, cancellationToken: ct);
            },
            (this, _compactedEntryId, _partitionKey, cancellationToken)).ConfigureAwait(false);

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
            // Ignore, no compacted entry.
        }

        // Lastly we determine current state based on the log entries and the sequence numbers.

        using var feed = _container.GetItemQueryIterator<LogSummary>(
            new QueryDefinition(@"
                SELECT
                    COUNT(1) AS EntriesCount,
                    MAX(c.SequenceNumber) AS MaxSequenceNumber
                FROM c
                WHERE c.LogId = @logId AND c.EntryType = @entryType")
                   .WithParameter("@logId", _logId)
                   .WithParameter("@entryType", LogEntryType.Log),
            requestOptions: _requestOptions);

        LogSummary? summary = null;

        if (feed.HasMoreResults)
        {
            var feedResponse = await _executor.ExecuteOperation(static args =>
            {
                var (feed, ct) = args;
                return feed.ReadNextAsync(ct);
            },
            (feed, cancellationToken)).ConfigureAwait(false);

            summary = feedResponse.SingleOrDefault(); // There should not be more than 1 document.
        }

        if (summary != null)
        {
            _logEntriesCount = summary.EntriesCount;
            _nextSequenceNumber = (summary.MaxSequenceNumber ?? -1L) + 1;
        }
        else
        {
            _logEntriesCount = 0;
            _nextSequenceNumber = 0;
        }

        _isInitialized = true;
        _isCompacted = false;
        _compactedEntryETag = null;

        LogInitialized(_logger, _logId, _isCompacted, _logEntriesCount);
    }

    /// <summary>
    /// This does the compaction, which means it deletes old log entries, the pending compaction entry,
    /// and creates the new entry of type 'compacted'.
    /// </summary>
    private async Task CompactAsync(byte[] compactedData, string pendingEntryETag, CancellationToken cancellationToken)
    {
        // This is similar to DeleteAsync, but we exclude the pending compaction entry from the loop.
        // We delete the pending compaction afterwards (still within the same transactional batch) by using the ETag.
        // Lastly we add the new compacted entry (again in the same TX).
        
        // Note that we increment the 'entryCount' only for the deleted entries, and do NOT count the removal of
        // the pending entry and the newly created compaction once as part of that number, as the pending
        // is here to help faciliate this ability, and the compacted one should not count also.

        var batch = _container.CreateTransactionalBatch(_partitionKey);
        var entryCount = 0;

        using var feed = _container.GetItemQueryIterator<LogEntryId>(
            new QueryDefinition(@"
                SELECT c.id FROM c
                WHERE c.LogId = @logId AND c.EntryType != @entryType")
            .WithParameter("@logId", _logId)
            .WithParameter("@entryType", LogEntryType.CompactionPending),
                requestOptions: _requestOptions);

        while (feed.HasMoreResults)
        {
            var feedResponse = await _executor.ExecuteOperation(static args =>
            {
                var (feed, ct) = args;
                return feed.ReadNextAsync(ct);
            },
            (feed, cancellationToken)).ConfigureAwait(false);

            foreach (var entryId in feedResponse)
            {
                batch.DeleteItem(entryId.Value);
                entryCount++;
            }
        }

        batch.DeleteItem(_compactionPendingEntryId, new() { IfMatchEtag = pendingEntryETag });

        var newCompacted = new CosmosLogEntry
        {
            Id = _compactedEntryId,
            LogId = _logId,
            EntryType = LogEntryType.Compacted,
            Data = compactedData,
            SequenceNumber = 0
        };

        batch.CreateItem(newCompacted);

        var batchResponse = await _executor.ExecuteOperation(static args =>
        {
            var (batch, ct) = args;
            return batch.ExecuteAsync(ct);
        }, (batch, cancellationToken)).ConfigureAwait(false);

        if (!batchResponse.IsSuccessStatusCode)
        {
            LogErrorFinalizingCompaction(_logger, _logId, batchResponse.StatusCode.ToString(), batchResponse.ErrorMessage);

            throw new CosmosException($"Failed to finalize compaction for log {_logId}." +
                $"Status: {batchResponse.StatusCode}, Error: {batchResponse.ErrorMessage}",
                batchResponse.StatusCode, 0, batchResponse.ActivityId, batchResponse.RequestCharge);
        }

        LogFinalizedCompaction(_logger, _logId, entryCount);
    }
}
