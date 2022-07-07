using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    public class AzureTableTransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly TableClient table;
        private readonly string partition;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly ILogger logger;

        private KeyEntity key;
        private List<KeyValuePair<long, StateEntity>> states;

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

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            try
            {
                var keyTask = ReadKey();
                var statesTask = ReadStates();
                key = await keyTask.ConfigureAwait(false);
                states = await statesTask.ConfigureAwait(false);

                if (string.IsNullOrEmpty(key.ETag.ToString()))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("{Partition} Loaded v0, fresh", partition);

                    // first time load
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
                            logger.LogCritical($"{partition} {error}");
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
                            TransactionId = kvp.Value.TransactionId,
                            TransactionManager = tm
                        });
                    }

                    // clear the state strings... no longer needed, ok to GC now
                    for (int i = 0; i < states.Count; i++)
                    {
                        var entity = states[i].Value;
                        entity.StateJson = null;
                    }

                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("{PartitionKey} Loaded v{CommittedSequenceId} rows={Data}", partition, this.key.CommittedSequenceId, string.Join(",", states.Select(s => s.Key.ToString("x16"))));

                    TransactionalStateMetaData metadata = JsonConvert.DeserializeObject<TransactionalStateMetaData>(this.key.Metadata, this.jsonSettings);
                    return new TransactionalStorageLoadResponse<TState>(this.key.ETag.ToString(), committedState, this.key.CommittedSequenceId, metadata, PrepareRecordsToRecover);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Transactional state load failed");
                throw;
            }
        }


        public async Task<string> Store(string expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            var keyETag = key.ETag.ToString();
            if ((!string.IsNullOrWhiteSpace(keyETag) || !string.IsNullOrWhiteSpace(expectedETag)) && keyETag != expectedETag)
            {
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
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

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace("{PartitionKey}.{RowKey} Delete {TransactionId}", partition, entity.RowKey, entity.TransactionId);
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

                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace("{PartitionKey}.{RowKey} Update {TransactionId}", partition, existing.RowKey, existing.TransactionId);
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.jsonSettings, this.partition, s);
                            await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, entity.Entity)).ConfigureAwait(false);
                            key.ETag = batchOperation.KeyETag;
                            states.Insert(pos, new KeyValuePair<long, StateEntity>(s.SequenceId, entity));

                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace("{PartitionKey}.{RowKey} Insert {TransactionId}", partition, entity.RowKey, entity.TransactionId);
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

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("{PartitionKey}.{RowKey} Insert. v{CommittedSequenceId}, {CommitRecordsCount}c", partition, KeyEntity.RK, this.key.CommittedSequenceId, metadata.CommitRecords.Count);
            }
            else
            {
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, key, key.ETag)).ConfigureAwait(false);
                key.ETag = batchOperation.KeyETag;

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("{PartitionKey}.{RowKey} Update. v{CommittedSequenceId}, {CommitRecordsCount}c", partition, KeyEntity.RK, this.key.CommittedSequenceId, metadata.CommitRecords.Count);
            }

            // fourth, remove obsolete records
            if (states.Count > 0 && states[0].Key < obsoleteBefore)
            {
                FindState(obsoleteBefore, out var pos);
                for (int i = 0; i < pos; i++)
                {
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, states[i].Value.Entity, states[i].Value.ETag)).ConfigureAwait(false);
                    key.ETag = batchOperation.KeyETag;

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace("{PartitionKey}.{RowKey} Delete {TransactionId}", partition, states[i].Value.RowKey, states[i].Value.TransactionId);
                }
                states.RemoveRange(0, pos);
            }

            await batchOperation.Flush().ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("{PartitionKey} Stored v{CommittedSequenceId} eTag={ETag}", partition, this.key.CommittedSequenceId, key.ETag);

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

        private async Task<KeyEntity> ReadKey()
        {
            var queryResult = table.QueryAsync<KeyEntity>(AzureTableUtils.PointQuery(this.partition, KeyEntity.RK)).ConfigureAwait(false);
            await foreach (var result in queryResult)
            {
                return result;
            }

            return new KeyEntity()
            {
                PartitionKey = partition,
                RowKey = KeyEntity.RK
            };
        }

        private async Task<List<KeyValuePair<long, StateEntity>>> ReadStates()
        {
            var query = AzureTableUtils.RangeQuery(this.partition, StateEntity.RK_MIN, StateEntity.RK_MAX);
            var results = new List<KeyValuePair<long, StateEntity>>();
            var queryResult = table.QueryAsync<TableEntity>(query).ConfigureAwait(false);
            await foreach (var entity in queryResult)
            {
                var state = new StateEntity(entity);
                results.Add(new KeyValuePair<long, StateEntity>(state.SequenceId, state));
            };
            return results;
        }

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

                if (batchOperation.Count == AzureTableConstants.MaxBatchSize - (BatchHasKey ? 0 : 1))
                {
                    // the key serves as a synchronizer, to prevent modification by multiple grains under edge conditions,
                    // like duplicate activations or deployments.Every batch write needs to include the key,
                    // even if the key values don't change.

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

                    await Flush().ConfigureAwait(false);
                }
            }

            public async Task Flush()
            {
                if (batchOperation.Count > 0)
                {
                    try
                    {
                        var batchResponse = await table.SubmitTransactionAsync(batchOperation).ConfigureAwait(false);
                        if (batchResponse?.Value is { Count: > 0 } responses)
                        {
                            if (BatchHasKey && responses.Count >= keyIndex && responses[keyIndex].Headers.ETag is { } etag)
                            {
                                key.ETag = etag;
                            }
                        }

                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                            {
                                logger.LogTrace("{PartitionKey}.{RowKey} batch-op ok {BatchCount}", batchOperation[i].Entity.PartitionKey, batchOperation[i].Entity.RowKey, i);
                            }
                        }

                        batchOperation.Clear();
                        keyIndex = -1;
                    }
                    catch (Exception ex)
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                            {
                                logger.LogTrace("{PartitionKey}.{RowKey} batch-op failed {BatchCount}", batchOperation[i].Entity.PartitionKey, batchOperation[i].Entity.RowKey, i);
                            }
                        }

                        this.logger.LogError(ex, "Transactional state store failed.");
                        throw;
                    }
                }
            }
        }

    }
}
