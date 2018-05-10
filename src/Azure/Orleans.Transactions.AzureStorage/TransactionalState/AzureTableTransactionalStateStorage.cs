﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    public class AzureTableTransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly CloudTable table;
        private readonly string partition;
        private readonly string stateName;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly ILogger logger;

        private KeyEntity key;
        private List<KeyValuePair<long, StateEntity>> states;

        public AzureTableTransactionalStateStorage(CloudTable table, string partition, string stateName, JsonSerializerSettings JsonSettings, ILogger<AzureTableTransactionalStateStorage<TState>> logger)
        {
            this.table = table;
            this.partition = partition;
            this.stateName = stateName;
            this.jsonSettings = JsonSettings;
            this.logger = logger;
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            try
            {
                var keyTask = ReadKey();
                var statesTask = ReadStates();
                key = await keyTask.ConfigureAwait(false);
                states = await statesTask.ConfigureAwait(false);

                if (string.IsNullOrEmpty(key.ETag))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug($"{partition} Loaded v0, fresh");

                    // first time load
                    return new TransactionalStorageLoadResponse<TState>();
                }
                else
                {
                    if (!FindState(this.key.CommittedSequenceId, out var pos))
                    {
                        var error = $"Storage state corrupted: no record for committed state";
                        logger.LogCritical(error);
                        throw new InvalidOperationException(error);
                    }
                    var committedState = states[pos].Value.GetState<TState>(this.jsonSettings);

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

                        PrepareRecordsToRecover.Add(new PendingTransactionState<TState>()
                        {
                            SequenceId = kvp.Key,
                            State = kvp.Value.GetState<TState>(this.jsonSettings),
                            TimeStamp = kvp.Value.TransactionTimestamp,
                            TransactionId = kvp.Value.TransactionId,
                            TransactionManager = kvp.Value.TransactionManager
                        });
                    }

                    // clear the state strings... no longer needed, ok to GC now
                    for (int i = 0; i < states.Count; i++)
                    {
                        states[i].Value.StateJson = null;
                    }

                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug($"{partition} Loaded v{this.key.CommittedSequenceId} rows={string.Join(",", states.Select(s => s.Key.ToString("x16")))}");

                    return new TransactionalStorageLoadResponse<TState>(this.key.ETag, committedState, this.key.CommittedSequenceId, this.key.Metadata, PrepareRecordsToRecover);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError("Transactional state load failed {Exception}.", ex);
                throw;
            }
        }


        public async Task<string> Store(string expectedETag, string metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            if (this.key.ETag != expectedETag)
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");

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
                    await batchOperation.Add(TableOperation.Delete(entity)).ConfigureAwait(false);
                    states.RemoveAt(states.Count - 1);

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace($"{partition}.{states[states.Count - 1].Key:x16} Delete {entity.TransactionId}");
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
                            var existing = states[pos].Value;
                            existing.TransactionId = s.TransactionId;
                            existing.TransactionTimestamp = s.TimeStamp;
                            existing.TransactionManager = s.TransactionManager;
                            existing.SetState(s.State, this.jsonSettings);
                            await batchOperation.Add(TableOperation.Replace(existing)).ConfigureAwait(false);
                            states.RemoveAt(pos);

                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace($"{partition}.{existing.SequenceId:x16} Update {existing.TransactionId}");
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.jsonSettings, this.partition, s);
                            await batchOperation.Add(TableOperation.Insert(entity)).ConfigureAwait(false);
                            states.Insert(pos, new KeyValuePair<long, StateEntity>(s.SequenceId, entity));

                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace($"{partition}.{s.SequenceId:x16} Insert {entity.TransactionId}");
                        }
                    }

            // third, persist metadata and commit position
            key.Metadata = metadata;
            if (commitUpTo.HasValue && commitUpTo.Value > key.CommittedSequenceId)
            {
                key.CommittedSequenceId = commitUpTo.Value;
            }
            if (string.IsNullOrEmpty(this.key.ETag))
            {
                await batchOperation.Add(TableOperation.Insert(this.key)).ConfigureAwait(false);

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace($"{partition}.k Insert");
            }
            else
            {
                await batchOperation.Add(TableOperation.Replace(this.key)).ConfigureAwait(false);

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace($"{partition}.k Update");
            }

            // fourth, remove obsolete records
            if (states.Count > 0 && states[0].Key < obsoleteBefore)
            {
                FindState(obsoleteBefore, out var pos);
                for (int i = 0; i < pos; i++)
                {
                    await batchOperation.Add(TableOperation.Delete(states[i].Value)).ConfigureAwait(false);

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace($"{partition}.{states[i].Key:x16} Delete {states[i].Value.TransactionId}");
                }
                states.RemoveRange(0, pos);
            }

            await batchOperation.Flush().ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug($"{partition} Stored v{this.key.CommittedSequenceId} eTag={key.ETag}");

            return key.ETag;
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
                        break;
                }
            }
            return false;
        }

        private async Task<KeyEntity> ReadKey()
        {
            var query = new TableQuery<KeyEntity>()
                .Where(AzureStorageUtils.PointQuery(this.partition, KeyEntity.RK));
            TableQuerySegment<KeyEntity> queryResult = await table.ExecuteQuerySegmentedAsync(query, null).ConfigureAwait(false);
            return queryResult.Results.Count == 0
                ? new KeyEntity() { PartitionKey = this.partition }
                : queryResult.Results[0];
        }

        private async Task<List<KeyValuePair<long, StateEntity>>> ReadStates()
        {
            var query = new TableQuery<StateEntity>()
                .Where(AzureStorageUtils.RangeQuery(this.partition, StateEntity.RK_MIN, StateEntity.RK_MAX));
            TableContinuationToken continuationToken = null;
            var results = new List<KeyValuePair<long, StateEntity>>();
            do
            {
                TableQuerySegment<StateEntity> queryResult = await table.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                foreach (var x in queryResult.Results)
                {
                    results.Add(new KeyValuePair<long, StateEntity>(x.SequenceId, x));
                };
                continuationToken = queryResult.ContinuationToken;
            } while (continuationToken != null);
            return results;
        }

        private class BatchOperation
        {
            private readonly TableBatchOperation batchOperation;
            private readonly ILogger logger;
            private readonly KeyEntity key;
            private readonly CloudTable table;

            private bool batchContainsKey;

            public BatchOperation(ILogger logger, KeyEntity key, CloudTable table)
            {
                this.batchOperation = new TableBatchOperation();
                this.logger = logger;
                this.key = key;
                this.table = table;
            }

            public async Task Add(TableOperation operation)
            {
                batchOperation.Add(operation);

                if (operation.Entity == key)
                {
                    batchContainsKey = true;
                }

                if (batchOperation.Count == AzureTableConstants.MaxBatchSize - (batchContainsKey ? 0 : 1))
                {
                    // the key serves as a synchronizer, to prevent modification by multiple grains under edge conditions,
                    // like duplicate activations or deployments.Every batch write needs to include the key, 
                    // even if the key values don't change.

                    if (!batchContainsKey)
                    {
                        if (string.IsNullOrEmpty(key.ETag))
                            batchOperation.Insert(key);
                        else
                            batchOperation.Replace(key);
                    }

                    await Flush().ConfigureAwait(false);

                    batchOperation.Clear();
                    batchContainsKey = false;
                }
            }

            public async Task Flush()
            {
                if (batchOperation.Count > 0)
                    try
                    {
                        await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);

                        batchOperation.Clear();
                        batchContainsKey = false;

                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                                logger.LogTrace($"batch-op ok     {i} PK={batchOperation[i].Entity.PartitionKey} RK={batchOperation[i].Entity.RowKey}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            for (int i = 0; i < batchOperation.Count; i++)
                                logger.LogTrace($"batch-op failed {i} PK={batchOperation[i].Entity.PartitionKey} RK={batchOperation[i].Entity.RowKey}");
                        }

                        this.logger.LogError("Transactional state store failed {Exception}.", ex);
                        throw;
                    }
            }
        }

    }
}