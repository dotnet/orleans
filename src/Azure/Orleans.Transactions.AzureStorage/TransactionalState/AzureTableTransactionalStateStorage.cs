using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    /// <summary>
    /// Provides Azure Table Storage-backed transactional state storage.
    /// </summary>
    /// <typeparam name="TState">The transactional state type.</typeparam>
    public partial class AzureTableTransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private const int MaxSnapshotLoadAttempts = 5;
        private const string LowerBoundaryRowKey = "!";
        private const string UpperBoundaryRowKey = "~";
        private const string BoundaryVersionPropertyName = "SnapshotVersion";
        private const int BoundaryRowCount = 2;

        private readonly TableClient table;
        private readonly string partition;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly ILogger logger;

        private KeyEntity key = null!;
        private List<KeyValuePair<long, StateEntity>> states = null!;
        private bool _storeRequiresLoad;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureTableTransactionalStateStorage{TState}"/> class.
        /// </summary>
        /// <param name="table">The client for the table which stores transactional state.</param>
        /// <param name="partition">The partition key for the transactional state.</param>
        /// <param name="JsonSettings">The settings used to serialize transactional state.</param>
        /// <param name="logger">The logger.</param>
        public AzureTableTransactionalStateStorage(TableClient table, string partition, JsonSerializerSettings JsonSettings, ILogger<AzureTableTransactionalStateStorage<TState>> logger)
        {
            this.table = table;
            this.partition = partition;
            this.jsonSettings = JsonSettings;
            this.logger = logger;

            // default values must be included
            // otherwise, we get errors for explicitly specified default values
            // (e.g.  Orleans.Transactions.Azure.Tests.TestState.state)
            this.jsonSettings.DefaultValueHandling = DefaultValueHandling.Include;
        }

        /// <inheritdoc />
        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            try
            {
                (key, states) = await LoadSnapshot().ConfigureAwait(false);

                if (string.IsNullOrEmpty(key.ETag.ToString()))
                {
                    LogDebugLoadedV0Fresh(partition);

                    // first time load
                    _storeRequiresLoad = false;
                    return new TransactionalStorageLoadResponse<TState>();
                }
                else
                {
                    TState committedState;
                    if (this.key.CommittedSequenceId == 0)
                    {
                        committedState = new TState();
                    }
                    else
                    {
                        if (!FindState(this.key.CommittedSequenceId, out var pos))
                        {
                            var error = $"Storage state corrupted: no record for committed state v{this.key.CommittedSequenceId}";
                            LogCriticalPartitionError(partition, error);
                            throw new InvalidOperationException(error);
                        }
                        committedState = states[pos].Value.GetState<TState>(this.jsonSettings);
                    }

                    var PrepareRecordsToRecover = new List<PendingTransactionState<TState>>();
                    for (int i = 0; i < states.Count; i++)
                    {
                        var kvp = states[i];

                        // pending states for already committed transactions can be ignored
                        if (kvp.Key <= key.CommittedSequenceId)
                            continue;

                        // upon recovery, local non-committed transactions are considered aborted
                        if (kvp.Value.TransactionManager == null)
                            break;

                        ParticipantId tm = JsonConvert.DeserializeObject<ParticipantId>(kvp.Value.TransactionManager, this.jsonSettings);

                        PrepareRecordsToRecover.Add(new PendingTransactionState<TState>()
                        {
                            SequenceId = kvp.Key,
                            State = kvp.Value.GetState<TState>(this.jsonSettings),
                            TimeStamp = kvp.Value.TransactionTimestamp,
                            TransactionId = kvp.Value.TransactionId!, // Persisted transaction rows always include an identifier.
                            TransactionManager = tm
                        });
                    }

                    // clear the state strings... no longer needed, ok to GC now
                    for (int i = 0; i < states.Count; i++)
                    {
                        var entity = states[i].Value;
                        entity.StateJson = null;
                    }

                    LogDebugLoadedPartitionKeyRows(partition, this.key.CommittedSequenceId, new(states));

                    TransactionalStateMetaData metadata = JsonConvert.DeserializeObject<TransactionalStateMetaData>(this.key.Metadata!, this.jsonSettings)!;
                    var result = new TransactionalStorageLoadResponse<TState>(this.key.ETag.ToString(), committedState, this.key.CommittedSequenceId, metadata, PrepareRecordsToRecover);
                    _storeRequiresLoad = false;
                    return result;
                }
            }
            catch (Exception ex)
            {
                LogErrorTransactionalStateLoadFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
        public async Task<string> Store(string? expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>>? statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            if (_storeRequiresLoad)
            {
                throw new InvalidOperationException("Load must complete successfully before Store can be called again after a failed Store operation.");
            }

            var keyETag = key.ETag.ToString();
            if ((!string.IsNullOrWhiteSpace(keyETag) || !string.IsNullOrWhiteSpace(expectedETag)) && keyETag != expectedETag)
            {
                throw new ArgumentException("Etag does not match", nameof(expectedETag));
            }

            try
            {
                return await StoreCore(metadata, statesToPrepare, commitUpTo, abortAfter).ConfigureAwait(false);
            }
            catch
            {
                _storeRequiresLoad = true;
                throw;
            }
        }

        private async Task<string> StoreCore(TransactionalStateMetaData metadata, List<PendingTransactionState<TState>>? statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            if (string.IsNullOrEmpty(key.ETag.ToString()) && string.IsNullOrEmpty(key.Metadata))
            {
                // A split prepare can persist the fresh key before phase three publishes the incoming metadata.
                // Until then, the key must represent the valid previous (empty) committed state.
                key.Metadata = JsonConvert.SerializeObject(new TransactionalStateMetaData(), this.jsonSettings);
            }

            // assemble all storage operations into a single batch
            // these operations must commit in sequence, but not necessarily atomically
            // so we can split this up if needed
            var batchOperation = new BatchOperation(logger, key, table);

            // first, clean up aborted records
            if (abortAfter.HasValue && states.Count != 0)
            {
                while (states.Count > 0 && states[states.Count - 1].Key > abortAfter)
                {
                    var entity = states[states.Count - 1].Value;
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity.Entity, entity.ETag)).ConfigureAwait(false);
                    key.ETag = batchOperation.KeyETag;
                    states.RemoveAt(states.Count - 1);

                    LogTraceDeleteTransaction(partition, entity.RowKey);
                }
            }

            // second, persist non-obsolete prepare records
            var obsoleteBefore = commitUpTo.HasValue ? commitUpTo.Value : key.CommittedSequenceId;
            if (statesToPrepare != null)
                foreach (var s in statesToPrepare)
                    if (s.SequenceId >= obsoleteBefore)
                    {
                        if (FindState(s.SequenceId, out var pos))
                        {
                            // overwrite with new pending state
                            StateEntity existing = states[pos].Value;
                            existing.TransactionId = s.TransactionId;
                            existing.TransactionTimestamp = s.TimeStamp;
                            existing.TransactionManager = JsonConvert.SerializeObject(s.TransactionManager, this.jsonSettings);
                            existing.SetState(s.State, this.jsonSettings);
                            await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, existing.Entity, existing.ETag)).ConfigureAwait(false);
                            key.ETag = batchOperation.KeyETag;

                            LogTraceUpdateTransaction(partition, existing.RowKey);
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.jsonSettings, this.partition, s);
                            await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, entity.Entity)).ConfigureAwait(false);
                            key.ETag = batchOperation.KeyETag;
                            states.Insert(pos, new KeyValuePair<long, StateEntity>(s.SequenceId, entity));

                            LogTraceInsertTransaction(partition, entity.RowKey);
                        }
                    }

            // third, persist metadata and commit position
            key.Metadata = JsonConvert.SerializeObject(metadata, this.jsonSettings);
            if (commitUpTo.HasValue && commitUpTo.Value > key.CommittedSequenceId)
            {
                key.CommittedSequenceId = commitUpTo.Value;
            }
            if (string.IsNullOrEmpty(this.key.ETag.ToString()))
            {
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, key)).ConfigureAwait(false);
                key.ETag = batchOperation.KeyETag;

                LogTraceInsertWithCount(partition, KeyEntity.RK, this.key.CommittedSequenceId, metadata.CommitRecords.Count);
            }
            else
            {
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, key, key.ETag)).ConfigureAwait(false);
                key.ETag = batchOperation.KeyETag;

                LogTraceUpdateWithCount(partition, KeyEntity.RK, this.key.CommittedSequenceId, metadata.CommitRecords.Count);
            }

            // fourth, remove obsolete records
            if (states.Count > 0 && states[0].Key < obsoleteBefore)
            {
                FindState(obsoleteBefore, out var pos);
                for (int i = 0; i < pos; i++)
                {
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, states[i].Value.Entity, states[i].Value.ETag)).ConfigureAwait(false);
                    key.ETag = batchOperation.KeyETag;

                    LogTraceDeleteTransaction(partition, states[i].Value.RowKey);
                }
                states.RemoveRange(0, pos);
            }

            await batchOperation.Flush().ConfigureAwait(false);

            LogDebugStoredETag(partition, this.key.CommittedSequenceId, key.ETag);

            return key.ETag.ToString();
        }

        private bool FindState(long sequenceId, out int pos)
        {
            pos = 0;
            while (pos < states.Count)
            {
                switch (states[pos].Key.CompareTo(sequenceId))
                {
                    case 0:
                        return true;
                    case -1:
                        pos++;
                        continue;
                    case 1:
                        return false;
                }
            }
            return false;
        }

        private async Task<(KeyEntity Key, List<KeyValuePair<long, StateEntity>> States)> LoadSnapshot()
        {
            KeyEntity keyBefore = null!;
            string? versionBefore = null;
            string? versionAfter = null;
            for (var attempt = 0; attempt < MaxSnapshotLoadAttempts; attempt++)
            {
                (keyBefore, var loadedStates, var isPaginated, versionBefore, versionAfter) = await ReadSnapshot().ConfigureAwait(false);
                if (!isPaginated)
                {
                    // A single Query Entities response is one strongly consistent storage operation.
                    return (keyBefore, loadedStates);
                }

                if (versionBefore is null && versionAfter is null)
                {
                    // Legacy partitions do not have boundary rows. During rolling upgrades, older
                    // writers also leave existing boundaries stale, so torn reads remain allowed
                    // until every writer is upgraded to advance them.
                    return (keyBefore, loadedStates);
                }

                if (versionBefore is not null
                    && string.Equals(versionBefore, versionAfter, StringComparison.Ordinal))
                {
                    return (keyBefore, loadedStates);
                }
            }

            throw new InconsistentStateException(
                "Could not load a consistent Azure Table transactional state snapshot.",
                storedEtag: versionBefore ?? "null",
                currentEtag: versionAfter ?? "null");
        }

        private async Task<(
            KeyEntity Key,
            List<KeyValuePair<long, StateEntity>> States,
            bool IsPaginated,
            string? LowerBoundaryVersion,
            string? UpperBoundaryVersion)> ReadSnapshot()
        {
            var query = AzureTableUtils.RangeQuery(this.partition, LowerBoundaryRowKey, UpperBoundaryRowKey);
            var key = CreateFreshKey();
            var states = new List<KeyValuePair<long, StateEntity>>();
            var pageCount = 0;
            string? lowerBoundaryVersion = null;
            string? upperBoundaryVersion = null;
            await foreach (var page in table.QueryAsync<TableEntity>(query).AsPages().ConfigureAwait(false))
            {
                pageCount++;
                foreach (var entity in page.Values)
                {
                    if (entity.RowKey == KeyEntity.RK)
                    {
                        key = new KeyEntity
                        {
                            PartitionKey = entity.PartitionKey,
                            RowKey = entity.RowKey,
                            Timestamp = entity.Timestamp,
                            ETag = entity.ETag,
                            CommittedSequenceId = entity.GetInt64(nameof(KeyEntity.CommittedSequenceId)).GetValueOrDefault(),
                            Metadata = entity.GetString(nameof(KeyEntity.Metadata))
                        };
                    }
                    else if (entity.RowKey.StartsWith(StateEntity.RK_PREFIX, StringComparison.Ordinal))
                    {
                        var state = new StateEntity(entity);
                        states.Add(new KeyValuePair<long, StateEntity>(state.SequenceId, state));
                    }
                    else if (entity.RowKey == LowerBoundaryRowKey)
                    {
                        lowerBoundaryVersion = entity.GetString(BoundaryVersionPropertyName);
                    }
                    else if (entity.RowKey == UpperBoundaryRowKey)
                    {
                        upperBoundaryVersion = entity.GetString(BoundaryVersionPropertyName);
                    }
                }
            }

            return (key, states, pageCount > 1, lowerBoundaryVersion, upperBoundaryVersion);
        }

        private KeyEntity CreateFreshKey()
            => new()
            {
                PartitionKey = partition,
                RowKey = KeyEntity.RK
            };

        private class BatchOperation
        {
            private readonly List<TableTransactionAction> batchOperation;
            private readonly ILogger logger;
            private readonly TableClient table;
            private KeyEntity key;

            private int keyIndex = -1;

            public BatchOperation(ILogger logger, KeyEntity key, TableClient table)
            {
                this.batchOperation = new();
                this.logger = logger;
                this.key = key;
                this.table = table;
            }

            public ETag KeyETag => key.ETag;
            private bool BatchHasKey => keyIndex >= 0;

            public async ValueTask Add(TableTransactionAction operation)
            {
                if (!BatchHasKey && operation.Entity.RowKey == key.RowKey && operation.Entity.PartitionKey == key.PartitionKey)
                {
                    key = (KeyEntity)operation.Entity;
                    keyIndex = batchOperation.Count;
                }

                batchOperation.Add(operation);

                if (batchOperation.Count == AzureTableConstants.MaxBatchSize - BoundaryRowCount - (BatchHasKey ? 0 : 1))
                {
                    // the key serves as a synchronizer, to prevent modification by multiple grains under edge conditions,
                    // like duplicate activations or deployments. The boundary rows fence paginated reads.
                    await Flush().ConfigureAwait(false);
                }
            }

            public async Task Flush()
            {
                if (batchOperation.Count > 0)
                {
                    if (!BatchHasKey)
                    {
                        keyIndex = batchOperation.Count;
                        if (string.IsNullOrEmpty(key.ETag.ToString()))
                        {
                            batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, key));
                        }
                        else
                        {
                            batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, key, key.ETag));
                        }
                    }

                    var boundaryVersion = Guid.NewGuid().ToString("N");
                    batchOperation.Add(new TableTransactionAction(
                        TableTransactionActionType.UpsertReplace,
                        CreateBoundaryEntity(key.PartitionKey, LowerBoundaryRowKey, boundaryVersion)));
                    batchOperation.Add(new TableTransactionAction(
                        TableTransactionActionType.UpsertReplace,
                        CreateBoundaryEntity(key.PartitionKey, UpperBoundaryRowKey, boundaryVersion)));

                    try
                    {
                        var batchResponse = await table.SubmitTransactionAsync(batchOperation).ConfigureAwait(false);
                        if (batchResponse?.Value is { Count: > 0 } responses)
                        {
                            if (BatchHasKey && responses.Count > keyIndex && responses[keyIndex].Headers.ETag is { } etag)
                            {
                                key.ETag = etag;
                            }
                        }

                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                            {
                                LogTraceBatchOpOk(logger, batchOperation[i].Entity.PartitionKey, batchOperation[i].Entity.RowKey, i);
                            }
                        }

                        batchOperation.Clear();
                        keyIndex = -1;
                    }
                    catch (Exception ex) when (IsStorageConflict(ex))
                    {
                        var requestFailedException = GetStorageConflict(ex)!;
                        var failedOperationIndex = requestFailedException is TableTransactionFailedException transactionFailedException
                            ? transactionFailedException.FailedTransactionActionIndex
                            : null;
                        var actionIndex = failedOperationIndex is >= 0 && failedOperationIndex < batchOperation.Count
                            ? failedOperationIndex
                            : batchOperation.Count == 1 ? 0 : null;
                        var action = actionIndex.HasValue ? batchOperation[actionIndex.Value] : null;
                        var actionIndexText = actionIndex?.ToString() ?? "Unavailable";
                        var actionType = action?.ActionType.ToString() ?? "Unavailable";
                        var rowKey = action?.Entity.RowKey ?? "Unavailable";
                        var errorCode = requestFailedException.ErrorCode ?? "Unavailable";
                        var failedOperationIndexText = failedOperationIndex?.ToString() ?? "Unavailable";

                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                            {
                                LogTraceBatchOpFailed(
                                    logger,
                                    batchOperation[i].Entity.PartitionKey,
                                    batchOperation[i].Entity.RowKey,
                                    i,
                                    batchOperation[i].ActionType,
                                    requestFailedException.Status,
                                    errorCode,
                                    failedOperationIndexText);
                            }
                        }

                        LogErrorTransactionalStateStoreConflict(
                            logger,
                            key.PartitionKey,
                            actionIndexText,
                            actionType,
                            rowKey,
                            requestFailedException.Status,
                            errorCode,
                            failedOperationIndexText);

                        throw new InconsistentStateException(
                            $"Azure Table transactional state storage conflict. Partition={key.PartitionKey} ActionIndex={actionIndexText} ActionType={actionType} RowKey={rowKey} HttpStatus={requestFailedException.Status} ErrorCode={errorCode} FailedOperationIndex={failedOperationIndexText}",
                            "Unknown",
                            key.ETag.ToString());
                    }
                    catch (Exception ex)
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                            {
                                LogTraceBatchOpFailed(
                                    logger,
                                    batchOperation[i].Entity.PartitionKey,
                                    batchOperation[i].Entity.RowKey,
                                    i,
                                    batchOperation[i].ActionType,
                                    ex is RequestFailedException requestFailedException ? requestFailedException.Status : 0,
                                    ex is RequestFailedException { ErrorCode: { } errorCode } ? errorCode : "Unavailable",
                                    "Unavailable");
                            }
                        }

                        LogErrorTransactionalStateStoreFailed(logger, ex);
                        throw;
                    }
                }
            }

            private static TableEntity CreateBoundaryEntity(string partitionKey, string rowKey, string version)
                => new(partitionKey, rowKey)
                {
                    [BoundaryVersionPropertyName] = version
                };

            private static bool IsStorageConflict(Exception? exception)
                => GetStorageConflict(exception) is not null;

            private static RequestFailedException? GetStorageConflict(Exception? exception)
            {
                RequestFailedException? result = null;
                while (exception is not null)
                {
                    if (exception is RequestFailedException requestFailedException
                        && requestFailedException.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.PreconditionFailed)
                    {
                        result = requestFailedException;
                        if (requestFailedException is TableTransactionFailedException)
                        {
                            break;
                        }
                    }

                    exception = exception.InnerException;
                }

                return result;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{Partition} Loaded v0, fresh"
        )]
        private partial void LogDebugLoadedV0Fresh(string partition);

        [LoggerMessage(
            Level = LogLevel.Critical,
            Message = "{Partition} {Error}"
        )]
        private partial void LogCriticalPartitionError(string partition, string error);

        private readonly struct StatesLogRecord(List<KeyValuePair<long, StateEntity>> states)
        {
            public override string ToString() => string.Join(",", states.Select(s => s.Key.ToString("x16")));
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{PartitionKey} Loaded v{CommittedSequenceId} rows={Data}"
        )]
        private partial void LogDebugLoadedPartitionKeyRows(string partitionKey, long committedSequenceId, StatesLogRecord data);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Transactional state load failed"
        )]
        private partial void LogErrorTransactionalStateLoadFailed(Exception ex);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} Delete"
        )]
        private partial void LogTraceDeleteTransaction(string partitionKey, string rowKey);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} Update"
        )]
        private partial void LogTraceUpdateTransaction(string partitionKey, string rowKey);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} Insert"
        )]
        private partial void LogTraceInsertTransaction(string partitionKey, string rowKey);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} Insert. v{CommittedSequenceId}, {CommitRecordsCount}c"
        )]
        private partial void LogTraceInsertWithCount(string partitionKey, string rowKey, long committedSequenceId, int commitRecordsCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} Update. v{CommittedSequenceId}, {CommitRecordsCount}c"
        )]
        private partial void LogTraceUpdateWithCount(string partitionKey, string rowKey, long committedSequenceId, int commitRecordsCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{PartitionKey} Stored v{CommittedSequenceId} eTag={ETag}"
        )]
        private partial void LogDebugStoredETag(string partitionKey, long committedSequenceId, ETag eTag);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} batch-op ok {BatchCount}"
        )]
        private static partial void LogTraceBatchOpOk(ILogger logger, string partitionKey, string rowKey, int batchCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{PartitionKey}.{RowKey} batch-op failed {BatchCount} ActionType={ActionType} HttpStatus={HttpStatus} ErrorCode={ErrorCode} FailedOperationIndex={FailedOperationIndex}"
        )]
        private static partial void LogTraceBatchOpFailed(
            ILogger logger,
            string partitionKey,
            string rowKey,
            int batchCount,
            TableTransactionActionType actionType,
            int httpStatus,
            string errorCode,
            string failedOperationIndex);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Azure Table transactional state storage conflict. Partition={PartitionKey} ActionIndex={ActionIndex} ActionType={ActionType} RowKey={RowKey} HttpStatus={HttpStatus} ErrorCode={ErrorCode} FailedOperationIndex={FailedOperationIndex}"
        )]
        private static partial void LogErrorTransactionalStateStoreConflict(
            ILogger logger,
            string partitionKey,
            string actionIndex,
            string actionType,
            string rowKey,
            int httpStatus,
            string errorCode,
            string failedOperationIndex);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Transactional state store failed."
        )]
        private static partial void LogErrorTransactionalStateStoreFailed(ILogger logger, Exception ex);
    }
}
