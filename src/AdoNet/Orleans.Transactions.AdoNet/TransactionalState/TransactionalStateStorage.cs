using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Entity;
using Orleans.Transactions.AdoNet.Storage;
using IsolationLevel = System.Transactions.IsolationLevel;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    public class TransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly string stateId;
        private readonly TransactionalStateStorageOptions options;
        private readonly ILogger<TransactionalStateStorage<TState>> logger;
        private List<KeyValuePair<long, StateEntity>> stateEntityList;
        private KeyEntity keyEntity;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly IRelationalStorage storage;


        public TransactionalStateStorage(
            string stateId,
             JsonSerializerSettings jsonSettings,
            TransactionalStateStorageOptions options,
            ILogger<TransactionalStateStorage<TState>> logger)
        {
            this.stateId = stateId;//state position
            this.options = options;
            this.logger = logger;
            this.jsonSettings = jsonSettings;
            this.jsonSettings.DefaultValueHandling = DefaultValueHandling.Include;
            this.storage = RelationalStorage.CreateInstance(this.options.Invariant, this.options.ConnectionString);
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            keyEntity = await ReadKey();
            stateEntityList = await ReadStates();

            keyEntity = keyEntity ?? new KeyEntity()
            {
                StateId = stateId,
            };

            if (string.IsNullOrEmpty(keyEntity.ETag))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug($"{stateId} Loaded v0, fresh");

                return new TransactionalStorageLoadResponse<TState>();
            }

            TState committedState;
            if (keyEntity.CommittedSequenceId == 0)
            {
                committedState = new TState();
            }
            else
            {
                if (!FindState(keyEntity.CommittedSequenceId, out var pos))
                {
                    var error = $"Storage state corrupted: no record for committed state v{keyEntity.CommittedSequenceId}";
                    logger.LogCritical($"{stateId} {error}");
                    throw new InvalidOperationException(error);
                }

                committedState = JsonConvert.DeserializeObject<TState>(stateEntityList[pos].Value.StateJson);
            }

            var prepareRecordsToRecover = new List<PendingTransactionState<TState>>();
            for (var i = 0; i < stateEntityList.Count; i++)
            {
                var kvp  = stateEntityList[i];

                // pending states for already committed transactions can be ignored
                if (kvp.Key <= keyEntity.CommittedSequenceId)
                    continue;

                // upon recovery, local non-committed transactions are considered aborted
                if (kvp.Value.TransactionManager == null)
                    break;

                ParticipantId tm = JsonConvert.DeserializeObject<ParticipantId>(kvp.Value.TransactionManager, this.jsonSettings);

                prepareRecordsToRecover.Add(new PendingTransactionState<TState>()
                {
                    State = JsonConvert.DeserializeObject<TState>(kvp.Value.StateJson),
                    SequenceId = kvp.Key,
                    TimeStamp = kvp.Value.TransactionTimestamp,
                    TransactionId = kvp.Value.TransactionId,
                    TransactionManager = tm
                });
            }

            // clear the state value... no longer needed, ok to GC now
            foreach (var state in stateEntityList)
            {
                state.Value.StateJson = null;
            }

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug($"{stateId} Loaded v{keyEntity.CommittedSequenceId} rows={string.Join(",", stateEntityList.Select(s => s.Value.SequenceId.ToString("x16")))}");

            var meta = JsonConvert.DeserializeObject<TransactionalStateMetaData>(keyEntity.Metadata, jsonSettings);
            return new TransactionalStorageLoadResponse<TState>(
                keyEntity.ETag,
                committedState,
                keyEntity.CommittedSequenceId,
                meta,
                prepareRecordsToRecover);

        }

        public async Task<string> Store(
            string expectedETag,
            TransactionalStateMetaData metadata,
            List<PendingTransactionState<TState>> statesToPrepare,
            long? commitUpTo,
            long? abortAfter)
        {
            var keyETag = keyEntity.ETag;
            if ((!string.IsNullOrWhiteSpace(keyETag) || !string.IsNullOrWhiteSpace(expectedETag)) && keyETag != expectedETag)
            {
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
            }

            var batchOperation = new DbBatchOperation(stateId,keyEntity, this.options,logger);

            // first, clean up aborted records
            if (abortAfter.HasValue && stateEntityList.Count != 0)
            {
                while (stateEntityList.Count > 0 && stateEntityList[stateEntityList.Count - 1].Key > abortAfter)
                {
                    var entity = stateEntityList[stateEntityList.Count - 1];
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity.Value,entity.Value.ETag));
                    keyETag = batchOperation.KeyETag;
                    stateEntityList.RemoveAt(stateEntityList.Count - 1);

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace($"{stateId} Delete {entity.Value.TransactionId}");
                }
            }

            // second, persist non-obsolete prepare records
            var obsoleteBefore = commitUpTo.HasValue ? commitUpTo.Value : keyEntity.CommittedSequenceId;
            if (statesToPrepare != null)
            {
                foreach (var s in statesToPrepare)
                {
                    if (s.SequenceId >= obsoleteBefore)
                    {
                        if (FindState(s.SequenceId, out var pos))
                        {
                            // overwrite with new pending state
                            StateEntity existing = stateEntityList[pos].Value;
                            existing.TransactionId = s.TransactionId;
                            existing.TransactionTimestamp = s.TimeStamp;
                            existing.TransactionManager = JsonConvert.SerializeObject(s.TransactionManager, jsonSettings);
                            existing.StateJson = JsonConvert.SerializeObject(s.State, jsonSettings);
                            existing.Timestamp = DateTime.Now;
                            await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, existing,existing.ETag)).ConfigureAwait(false);
                            keyETag = batchOperation.KeyETag;
                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace($"{stateId} Update {existing.TransactionId}");
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.jsonSettings, this.stateId, s);
                            await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, entity)).ConfigureAwait(false);
                            keyETag = entity.ETag;
                            stateEntityList.Insert(pos, new KeyValuePair<long, StateEntity>(s.SequenceId, entity));

                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace($"{stateId} Insert {entity.TransactionId}");
                        }

                    }
                }
            }

            // third, persist metadata and commit position
            keyEntity.Metadata = JsonConvert.SerializeObject(metadata, jsonSettings);
            if (commitUpTo.HasValue && commitUpTo.Value > keyEntity.CommittedSequenceId)
            {
                keyEntity.CommittedSequenceId = commitUpTo.Value;
            }
            if (string.IsNullOrEmpty(keyEntity.ETag))
            {
                keyEntity.ETag = keyETag ?? Guid.NewGuid().ToString();
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, keyEntity)).ConfigureAwait(false);

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace($"{stateId} Insert. v{keyEntity.CommittedSequenceId}, {metadata.CommitRecords.Count}c");
            }
            else
            {
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, keyEntity,keyEntity.ETag)).ConfigureAwait(false);
                keyEntity.ETag = batchOperation.KeyETag;
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace($"{stateId} Update. v{keyEntity.CommittedSequenceId}, {metadata.CommitRecords.Count}c");
            }

            // fourth, remove obsolete records
            if (stateEntityList.Count > 0 && stateEntityList[0].Key < obsoleteBefore)
            {
                FindState(obsoleteBefore, out var pos);
                for (var i = 0; i < pos; i++)
                {
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, stateEntityList[i].Value, stateEntityList[i].Value.ETag)).ConfigureAwait(false);
                    keyEntity.ETag = batchOperation.KeyETag;

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace($"{stateId}.{stateEntityList[i].Key} Delete {stateEntityList[i].Value.TransactionId}");
                }
                stateEntityList.RemoveRange(0, pos);
            }

            await batchOperation.Flush().ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug($"{stateId} Stored v{keyEntity.CommittedSequenceId} eTag={keyEntity.ETag}");

            return keyEntity.ETag;
        }

        private bool FindState(long sequenceId, out int pos)
        {
            pos = 0;
            while (pos < stateEntityList.Count)
            {
                switch (stateEntityList[pos].Key.CompareTo(sequenceId))
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
            string myquert = $"SELECT * FROM {this.options.KeyEntityTableName} WHERE {nameof(keyEntity.StateId)}={this.options.SqlParameterDot}{nameof(keyEntity.StateId)}";

            var queryResult = await this.storage.ReadAsync<KeyEntity>(myquert, command =>
              {
                  command.AddParameter(nameof(keyEntity.StateId), stateId);
              }, (selector, resultSetCount, token) => Task.FromResult(GetConvertKeyRecord(selector)),
              cancellationToken: CancellationToken.None).ConfigureAwait(false);

           foreach (var result in queryResult)
            {
                return result;
            }
            // if no data return default
            return new KeyEntity()
            {
                StateId = stateId,
                RowKey = KeyEntity.RK
            };
        }

        private KeyEntity GetConvertKeyRecord(IDataRecord record)
        {
            var keyEntity = new KeyEntity()
            {
                StateId = record.GetValue<string>(nameof(KeyEntity.StateId)),
                ETag = record.GetValueOrDefault<string>(nameof(KeyEntity.ETag)),
                CommittedSequenceId = record.GetInt64(nameof(KeyEntity.CommittedSequenceId)),
                Timestamp = record.GetDateTimeValueOrDefault(nameof(KeyEntity.Timestamp)),
                Metadata = record.GetValueOrDefault<string>(nameof(KeyEntity.Metadata)),
            };
            return keyEntity;
        }


        private async Task<List<KeyValuePair<long, StateEntity>>> ReadStates()
        {
            string myquert = $"SELECT * FROM {this.options.StateEntityTableName} WHERE {nameof(StateEntity.StateId)}={this.options.SqlParameterDot}{nameof(StateEntity.StateId)} Order By {nameof(StateEntity.RowKey)}";
            var queryResult = await this.storage.ReadAsync<StateEntity>(myquert, command =>
            {
                command.AddParameter(nameof(StateEntity.StateId), stateId);
            }, (selector, resultSetCount, token) => Task.FromResult(GetConvertStateRecord(selector)),
           cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var results = new List<KeyValuePair<long, StateEntity>>();
            foreach (var entity in queryResult)
            {
                entity.SequenceId = StateEntity.GetSequenceId(entity.RowKey);
                results.Add(new KeyValuePair<long, StateEntity>(entity.SequenceId, entity));
            };
            return results;
        }

        private StateEntity GetConvertStateRecord(IDataRecord record)
        {
            var stateEntity = new StateEntity()
            {
                ETag = record.GetValueOrDefault<string>(nameof(StateEntity.ETag)),
                StateId = record.GetValue<string>(nameof(StateEntity.StateId)),
                TransactionId = record.GetValue<string>(nameof(StateEntity.TransactionId)),
                TransactionTimestamp = record.GetDateTimeValueOrDefault(nameof(StateEntity.TransactionTimestamp)).Value,
                TransactionManager = record.GetValue<string>(nameof(StateEntity.TransactionManager)),
                StateJson = record.GetValue<string>(nameof(StateEntity.StateJson)),
                RowKey = record.GetValueOrDefault<string>(nameof(StateEntity.RowKey)),
                Timestamp = record.GetDateTimeValueOrDefault(nameof(StateEntity.Timestamp)).Value,
            };
            return stateEntity;
        }



    }

    internal class DbBatchOperation
    {
        private readonly IRelationalStorage storage;
        readonly TransactionalStateStorageOptions options;
        readonly ILogger logger;
        readonly string stateId;
        private KeyEntity key;
        readonly int MaxBatchSize = 128;
        private int keyIndex = -1;


        List<TableTransactionAction> batchOperation = new List<TableTransactionAction>();

        public DbBatchOperation(
            string stateId,
             KeyEntity key,
            TransactionalStateStorageOptions options,
            ILogger logger
            )
        {
            this.options = options;
            this.logger = logger;
            this.stateId = stateId;
            this.key = key;
            this.storage = RelationalStorage.CreateInstance(this.options.Invariant, this.options.ConnectionString);
        }

        public string KeyETag => key.ETag;

        private bool BatchHasKey => keyIndex >= 0;

        public async ValueTask Add(TableTransactionAction operation)
        {
            if ((operation.Key != null && operation.Key.StateId != stateId) || (operation.State != null && operation.State.StateId != stateId))
            {
                throw new ArgumentException($"StateId not match.");
            }

            if (!BatchHasKey && operation.Key != null && operation.Key.RowKey == key.RowKey &&  operation.Key.StateId == stateId)
            {
                key = operation.Key;
                keyIndex = batchOperation.Count;
            }


            batchOperation.Add(operation);

            if (batchOperation.Count == MaxBatchSize - (BatchHasKey ? 0 : 1))
            {
                // the key serves as a synchronizer, to prevent modification by multiple grains under edge conditions,
                // like duplicate activations or deployments.Every batch write needs to include the key,
                // even if the key values don't change.

                if (!BatchHasKey)
                {
                    keyIndex = batchOperation.Count;
                    batchOperation.Add(new TableTransactionAction(operation.ActionType, key, key.ETag));
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

                    await SubmitTransactionAsync(batchOperation).ConfigureAwait(false);

                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        for (var i = 0; i < batchOperation.Count; i++)
                        {
                            logger.LogTrace($"{batchOperation[i].Key.StateId} batch-op ok     {i}");
                        }
                    }
                    batchOperation.Clear();
                    keyIndex = -1;
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        for (var i = 0; i < batchOperation.Count; i++)
                        {
                            logger.LogTrace($"{batchOperation[i].Key.StateId} batch-op failed {i}");
                        }
                    }

                    logger.LogError("Transactional state store failed {Exception}.", ex);
                    throw;
                }
            }

        }

        public async Task SubmitTransactionAsync(List<TableTransactionAction> list)
        {
            if (list == null || list.Count < 1)
            {
                return;
            }
            var addKeySql =  $"INSERT INTO {this.options.KeyEntityTableName} ({nameof(KeyEntity.StateId)},{nameof(KeyEntity.RowKey)},{nameof(KeyEntity.CommittedSequenceId)},{nameof(KeyEntity.Metadata)},{nameof(KeyEntity.Timestamp)},{nameof(KeyEntity.ETag)}) VALUES ({this.options.SqlParameterDot}{nameof(KeyEntity.StateId)},{this.options.SqlParameterDot}{nameof(KeyEntity.RowKey)},{this.options.SqlParameterDot}{nameof(KeyEntity.CommittedSequenceId)},{this.options.SqlParameterDot}{nameof(KeyEntity.Metadata)},{this.options.SqlParameterDot}{nameof(KeyEntity.Timestamp)},{this.options.SqlParameterDot}{nameof(KeyEntity.ETag)});";
            string updateKeySql = $"UPDATE {this.options.KeyEntityTableName} SET {nameof(KeyEntity.CommittedSequenceId)}={this.options.SqlParameterDot}{nameof(KeyEntity.CommittedSequenceId)},{nameof(KeyEntity.Metadata)}={this.options.SqlParameterDot}{nameof(KeyEntity.Metadata)},{nameof(KeyEntity.Timestamp)}={this.options.SqlParameterDot}{nameof(KeyEntity.Timestamp)} WHERE {nameof(KeyEntity.StateId)}={this.options.SqlParameterDot}{nameof(KeyEntity.StateId)} AND {nameof(KeyEntity.ETag)}={this.options.SqlParameterDot}{nameof(KeyEntity.ETag)};";
            string delKeySql = $"DELETE FROM {this.options.KeyEntityTableName} WHERE {nameof(KeyEntity.StateId)}={this.options.SqlParameterDot}{nameof(KeyEntity.StateId)};";

            string addStateSql= $"INSERT INTO {this.options.StateEntityTableName} ({nameof(StateEntity.StateId)},{nameof(StateEntity.TransactionId)},{nameof(StateEntity.TransactionTimestamp)},{nameof(StateEntity.TransactionManager)},{nameof(StateEntity.StateJson)},{nameof(StateEntity.RowKey)},{nameof(StateEntity.Timestamp)},{nameof(StateEntity.ETag)}) VALUES ({this.options.SqlParameterDot}{nameof(StateEntity.StateId)},{this.options.SqlParameterDot}{nameof(StateEntity.TransactionId)},{this.options.SqlParameterDot}{nameof(StateEntity.TransactionTimestamp)},{this.options.SqlParameterDot}{nameof(StateEntity.TransactionManager)},{this.options.SqlParameterDot}{nameof(StateEntity.StateJson)},{this.options.SqlParameterDot}{nameof(StateEntity.RowKey)},{this.options.SqlParameterDot}{nameof(StateEntity.Timestamp)},{this.options.SqlParameterDot}{nameof(StateEntity.ETag)});";
            string updateStateSql = $"UPDATE {this.options.StateEntityTableName} SET {nameof(StateEntity.TransactionId)}={this.options.SqlParameterDot}{nameof(StateEntity.TransactionId)},{nameof(StateEntity.TransactionTimestamp)}={this.options.SqlParameterDot}{nameof(StateEntity.TransactionTimestamp)},{nameof(StateEntity.TransactionManager)}={this.options.SqlParameterDot}{nameof(StateEntity.TransactionManager)},{nameof(StateEntity.StateJson)}={this.options.SqlParameterDot}{nameof(StateEntity.StateJson)},{nameof(StateEntity.Timestamp)}={this.options.SqlParameterDot}{nameof(StateEntity.Timestamp)} WHERE {nameof(StateEntity.StateId)}={this.options.SqlParameterDot}{nameof(StateEntity.StateId)} AND {nameof(StateEntity.RowKey)}={this.options.SqlParameterDot}{nameof(StateEntity.RowKey)};";
            string delStateSql = $"DELETE FROM {this.options.StateEntityTableName} WHERE {nameof(StateEntity.StateId)}={this.options.SqlParameterDot}{nameof(StateEntity.StateId)} AND {nameof(StateEntity.RowKey)}={this.options.SqlParameterDot}{nameof(StateEntity.RowKey)};";
            //  this.storage.wx
            using (TransactionScope scope = new TransactionScope(TransactionScopeOption.Required,
                   new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                   TransactionScopeAsyncFlowOption.Enabled))
            {
                //add,update,delete
                foreach (var transaction in list)
                {
                    if (transaction.Key != null)
                    {
                        transaction.Key.Timestamp = DateTime.Now;
                        switch (transaction.ActionType)
                        {
                            case TableTransactionActionType.Add:
                                 int affectedRowsCount = await storage.ExecuteAsync(addKeySql, command =>
                                 {
                                     command.AddParameter(nameof(KeyEntity.StateId), transaction.Key.StateId);
                                     command.AddParameter(nameof(KeyEntity.RowKey), transaction.Key.RowKey);
                                     command.AddParameter(nameof(KeyEntity.CommittedSequenceId), transaction.Key.CommittedSequenceId);
                                     command.AddParameter(nameof(KeyEntity.Metadata), transaction.Key.Metadata);
                                     command.AddParameter(nameof(KeyEntity.Timestamp), transaction.Key.Timestamp);
                                     command.AddParameter(nameof(KeyEntity.ETag), transaction.Key.ETag);
                                 }).ConfigureAwait(continueOnCapturedContext: false);
                                break;
                            case TableTransactionActionType.UpdateReplace:
                                int affectedRowsCount2 = await storage.ExecuteAsync(updateKeySql, command =>
                                {
                                    command.AddParameter(nameof(KeyEntity.CommittedSequenceId), transaction.Key.CommittedSequenceId);
                                    command.AddParameter(nameof(KeyEntity.Metadata), transaction.Key.Metadata);
                                    command.AddParameter(nameof(KeyEntity.Timestamp), transaction.Key.Timestamp);
                                    command.AddParameter(nameof(KeyEntity.StateId), transaction.Key.StateId);
                                    command.AddParameter(nameof(KeyEntity.ETag), transaction.Key.ETag);
                                }).ConfigureAwait(continueOnCapturedContext: false);
                                break;
                            case TableTransactionActionType.Delete:
                                int affectedRowsCount3 = await storage.ExecuteAsync(delKeySql, command =>
                                {
                                    command.AddParameter(nameof(KeyEntity.StateId), transaction.Key.StateId);
                                }).ConfigureAwait(continueOnCapturedContext: false);
                                break;
                            default:
                                break;
                        }
                    }
                    if (transaction.State != null)
                    {
                        switch (transaction.ActionType)
                        {
                            case TableTransactionActionType.Add:
                                int affectedRowsCount = await storage.ExecuteAsync(addStateSql, command =>
                                {
                                    command.AddParameter(nameof(StateEntity.StateId), transaction.State.StateId);
                                    command.AddParameter(nameof(StateEntity.TransactionId), transaction.State.TransactionId);
                                    command.AddParameter(nameof(StateEntity.TransactionTimestamp), transaction.State.TransactionTimestamp);
                                    command.AddParameter(nameof(StateEntity.TransactionManager), transaction.State.TransactionManager);
                                    command.AddParameter(nameof(StateEntity.StateJson), transaction.State.StateJson);
                                    command.AddParameter(nameof(StateEntity.RowKey), transaction.State.RowKey);
                                    command.AddParameter(nameof(StateEntity.Timestamp), transaction.State.Timestamp);
                                    command.AddParameter(nameof(StateEntity.ETag), transaction.State.ETag);
                                }).ConfigureAwait(continueOnCapturedContext: false);
                                break;
                            case TableTransactionActionType.UpdateReplace:               
                                int affectedRowsCount2 = await storage.ExecuteAsync(updateStateSql, command =>
                                {
                                    command.AddParameter(nameof(StateEntity.StateId), transaction.State.StateId);
                                    command.AddParameter(nameof(StateEntity.RowKey), transaction.State.RowKey);
                                    command.AddParameter(nameof(StateEntity.TransactionId), transaction.State.TransactionId);
                                    command.AddParameter(nameof(StateEntity.TransactionTimestamp), transaction.State.TransactionTimestamp);
                                    command.AddParameter(nameof(StateEntity.TransactionManager), transaction.State.TransactionManager);
                                    command.AddParameter(nameof(StateEntity.StateJson), transaction.State.StateJson);
                                    command.AddParameter(nameof(StateEntity.Timestamp), transaction.State.Timestamp);
                                }).ConfigureAwait(continueOnCapturedContext: false);
                                break;
                            case TableTransactionActionType.Delete: 
                                int affectedRowsCount3 = await storage.ExecuteAsync(delStateSql, command =>
                                {
                                    command.AddParameter(nameof(StateEntity.StateId), transaction.State.StateId);
                                    command.AddParameter(nameof(StateEntity.RowKey), transaction.State.RowKey);
                                }).ConfigureAwait(continueOnCapturedContext: false);
                                break;
                            default:
                                break;
                        }
                    }
                }

                // commit
                scope.Complete();
                scope.Dispose();
            }
           await Task.CompletedTask;
        }

    }
}
