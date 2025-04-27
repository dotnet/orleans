using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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
using Orleans.Transactions.AdoNet.Utils;

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
            this.stateId = stateId;
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

                committedState = JsonUtils.DeserializeWithNewtonsoftJson<TState>(stateEntityList[pos].Value.SateData);
            }

            var prepareRecordsToRecover = new List<PendingTransactionState<TState>>();
            for (var i = 0; i < stateEntityList.Count; i++)
            {
                var kvp = stateEntityList[i];

                // pending states for already committed transactions can be ignored
                if (kvp.Key <= keyEntity.CommittedSequenceId)
                    continue;

                // upon recovery, local non-committed transactions are considered aborted
                if (kvp.Value.TransactionManager == null)
                    break;

                ParticipantId tm = JsonUtils.DeserializeWithNewtonsoftJson<ParticipantId>(kvp.Value.TransactionManager);

                prepareRecordsToRecover.Add(new PendingTransactionState<TState>()
                {
                    State = JsonUtils.DeserializeWithNewtonsoftJson<TState>(kvp.Value.SateData),
                    SequenceId = kvp.Key,
                    TimeStamp = kvp.Value.TransactionTimestamp.Value.UtcDateTime,
                    TransactionId = kvp.Value.TransactionId,
                    TransactionManager = tm
                });
            }

            // clear the state value... no longer needed, ok to GC now
            foreach (var state in stateEntityList)
            {
                state.Value.SateData = null;
            }

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug($"{stateId} Loaded v{keyEntity.CommittedSequenceId} rows={string.Join(",", stateEntityList.Select(s => s.Value.SequenceId.ToString("x16")))}");

            var meta = JsonUtils.DeserializeWithNewtonsoftJson<TransactionalStateMetaData>(keyEntity.Metadata);
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

            var batchOperation = new DbBatchOperation(stateId, this.options, this.storage, logger);

            // first, clean up aborted records
            if (abortAfter.HasValue && stateEntityList.Count != 0)
            {
                while (stateEntityList.Count > 0 && stateEntityList[stateEntityList.Count - 1].Key > abortAfter)
                {
                    var entity = stateEntityList[stateEntityList.Count - 1];
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity.Value));
                    keyETag = entity.Value.ETag;
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
                            logger.LogWarning($"FindState:{expectedETag}");
                            // overwrite with new pending state
                            StateEntity existing = stateEntityList[pos].Value;
                            existing.TransactionId = s.TransactionId;
                            existing.TransactionTimestamp = new DateTimeOffset(s.TimeStamp).ToUniversalTime();
                            existing.TransactionManager = JsonUtils.SerializeWithNewtonsoftJson(s.TransactionManager, jsonSettings);
                            existing.SateData = JsonUtils.SerializeWithNewtonsoftJson(s.State, jsonSettings);
                            await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, existing)).ConfigureAwait(false);
                            keyETag = existing.ETag;
                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace($"{stateId} Update {existing.TransactionId}");
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.jsonSettings, this.stateId, s);
                            entity.ETag = string.IsNullOrWhiteSpace(keyETag) ? Guid.NewGuid().ToString() : keyETag;
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
            keyEntity.Metadata = JsonUtils.SerializeWithNewtonsoftJson(metadata, jsonSettings);
            if (commitUpTo.HasValue && commitUpTo.Value > keyEntity.CommittedSequenceId)
            {
                keyEntity.CommittedSequenceId = commitUpTo.Value;
            }
            if (string.IsNullOrEmpty(keyEntity.ETag))
            {
                keyEntity.ETag = string.IsNullOrWhiteSpace(keyETag) ? Guid.NewGuid().ToString() : keyETag;
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Add, keyEntity)).ConfigureAwait(false);

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace($"{stateId} Insert. v{keyEntity.CommittedSequenceId}, {metadata.CommitRecords.Count}c");
            }
            else
            {
                await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, keyEntity)).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace($"{stateId} Update. v{keyEntity.CommittedSequenceId}, {metadata.CommitRecords.Count}c");
            }

            // fourth, remove obsolete records
            if (stateEntityList.Count > 0 && stateEntityList[0].Key < obsoleteBefore)
            {
                FindState(obsoleteBefore, out var pos);
                for (var i = 0; i < pos; i++)
                {
                    await batchOperation.Add(new TableTransactionAction(TableTransactionActionType.Delete, stateEntityList[i].Value)).ConfigureAwait(false);

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
            string querySql = this.options.ExecuteSqlDcitionary[Constants.QueryKeySql];

            var queryResult = await this.storage.ReadAsync<KeyEntity>(querySql, command =>
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
            };
        }

        private KeyEntity GetConvertKeyRecord(IDataRecord record)
        {
            var keyEntity = new KeyEntity()
            {
                StateId = record.GetValue<string>(nameof(KeyEntity.StateId)),
                ETag = record.GetValueOrDefault<string>(nameof(KeyEntity.ETag)),
                CommittedSequenceId = record.GetInt64(nameof(KeyEntity.CommittedSequenceId)),
                //Timestamp = record.GetDateTimeValueOrDefault(nameof(KeyEntity.Timestamp)),
                Metadata = record.GetValueOrDefault<byte[]>(nameof(KeyEntity.Metadata)),
            };
            return keyEntity;
        }

        private async Task<List<KeyValuePair<long, StateEntity>>> ReadStates()
        {
            string querySql = this.options.ExecuteSqlDcitionary[Constants.QueryStateSql];

            var queryResult = await this.storage.ReadAsync<StateEntity>(querySql, command =>
            {
                command.AddParameter(nameof(StateEntity.StateId), stateId);
            }, (selector, resultSetCount, token) => Task.FromResult(GetConvertStateRecord(selector)),
           cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var results = new List<KeyValuePair<long, StateEntity>>();
            foreach (var entity in queryResult)
            {
                results.Add(new KeyValuePair<long, StateEntity>(entity.SequenceId, entity));
            };
            return results;
        }

        private StateEntity GetConvertStateRecord(IDataRecord record)
        {
            var stateEntity = new StateEntity()
            {
                StateId = record.GetValue<string>(nameof(StateEntity.StateId)),
                SequenceId = record.GetValueOrDefault<long>(nameof(StateEntity.SequenceId)),
                TransactionId = record.GetValue<string>(nameof(StateEntity.TransactionId)),
                TransactionTimestamp = record.GetDateTimeValueOrDefault(nameof(StateEntity.TransactionTimestamp)).Value,
                TransactionManager = record.GetValue<byte[]>(nameof(StateEntity.TransactionManager)),
                SateData = record.GetValue<byte[]>(nameof(StateEntity.SateData)),
                ETag = record.GetValueOrDefault<string>(nameof(StateEntity.ETag)),
                //Timestamp = record.GetDateTimeValueOrDefault(nameof(StateEntity.Timestamp)).Value,
            };
            return stateEntity;
        }
    }

    internal class DbBatchOperation
    {
        private readonly IRelationalStorage storage;
        private readonly TransactionalStateStorageOptions options;
        private readonly ILogger logger;
        private readonly string stateId;
        private readonly int MaxBatchSize = 128;

        private bool flushing = false;

        private List<TableTransactionAction> batchOperation = new List<TableTransactionAction>();

        public DbBatchOperation(
            string stateId,
            TransactionalStateStorageOptions options,
            IRelationalStorage storage,
            ILogger logger
            )
        {
            this.options = options;
            this.logger = logger;
            this.stateId = stateId;
            this.storage = storage;
        }

        public async ValueTask Add(TableTransactionAction operation)
        {
            if (operation.TableEntity == null)
            {
                return;
            }
            if (operation.TableEntity.StateId != stateId)
            {
                throw new ArgumentException($"StateId not match.");
            }

            if (string.IsNullOrEmpty(operation.TableEntity.ETag))
            {
                throw new ArgumentException($"{operation.TableEntity.StateId} ETag can not be null or empty");
            }

            batchOperation.Add(operation);

            if (batchOperation.Count >= MaxBatchSize)
            {
                await Flush().ConfigureAwait(false);
            }
        }

        public async Task Flush()
        {
            if (batchOperation.Count < 1 || flushing)
            {
                return;
            }

            if (batchOperation.Count > 0)
            {
                try
                {
                    await SubmitTransactionAsync(batchOperation).ConfigureAwait(false);

                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        for (var i = 0; i < batchOperation.Count; i++)
                        {
                            logger.LogTrace($"{batchOperation[i].TableEntity.StateId} batch-op ok     {i}");
                        }
                    }
                    batchOperation.Clear();
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        for (var i = 0; i < batchOperation.Count; i++)
                        {
                            logger.LogTrace($"{batchOperation[i].TableEntity.StateId} batch-op failed {i}");
                        }
                    }

                    logger.LogError("Transactional state store failed {Exception}.", ex);
                    throw;
                }
                finally
                {
                    flushing = false;
                }
            }
        }

        public async Task SubmitTransactionAsync(List<TableTransactionAction> list)
        {
            if (list == null || list.Count < 1)
            {
                return;
            }
            var addKeySql = this.options.ExecuteSqlDcitionary[Constants.AddKeySql];

            string updateKeySql = this.options.ExecuteSqlDcitionary[Constants.UpdateKeySql];

            string delKeySql = this.options.ExecuteSqlDcitionary[Constants.DelKeySql];

            string addStateSql = this.options.ExecuteSqlDcitionary[Constants.AddStateSql];

            string updateStateSql = this.options.ExecuteSqlDcitionary[Constants.UpdateStateSql];

            string delStateSql = this.options.ExecuteSqlDcitionary[Constants.DelStateSql];

            List<Tuple<string, Action<DbCommand>>> multipleQuery = new List<Tuple<string, Action<DbCommand>>>();

            //add,update,delete
            foreach (var transaction in list)
            {
                transaction.TableEntity.Timestamp = new DateTimeOffset(DateTime.Now).ToUniversalTime();
                if (transaction.TableEntity is KeyEntity)
                {
                    var keyData = transaction.TableEntity as KeyEntity;
                    switch (transaction.ActionType)
                    {
                        case TableTransactionActionType.Add:
                            multipleQuery.Add(new Tuple<string, Action<DbCommand>>(addKeySql, command =>
                            {
                                command.AddParameter(nameof(KeyEntity.StateId), keyData.StateId);
                                command.AddParameter(nameof(KeyEntity.CommittedSequenceId), keyData.CommittedSequenceId);
                                command.AddParameter(nameof(KeyEntity.Metadata), keyData.Metadata);
                                command.AddParameter(nameof(KeyEntity.Timestamp), keyData.Timestamp);
                                command.AddParameter(nameof(KeyEntity.ETag), keyData.ETag);
                            }));
                            break;

                        case TableTransactionActionType.UpdateReplace:
                            multipleQuery.Add(new Tuple<string, Action<DbCommand>>(updateKeySql, command =>
                            {
                                command.AddParameter(nameof(KeyEntity.CommittedSequenceId), keyData.CommittedSequenceId);
                                command.AddParameter(nameof(KeyEntity.Metadata), keyData.Metadata);
                                command.AddParameter(nameof(KeyEntity.Timestamp), keyData.Timestamp);
                                command.AddParameter(nameof(KeyEntity.StateId), keyData.StateId);
                                command.AddParameter(nameof(KeyEntity.ETag), keyData.ETag);
                            }));
                            break;

                        case TableTransactionActionType.Delete:
                            multipleQuery.Add(new Tuple<string, Action<DbCommand>>(delKeySql, command =>
                            {
                                command.AddParameter(nameof(KeyEntity.StateId), keyData.StateId);
                                command.AddParameter(nameof(KeyEntity.ETag), keyData.ETag);
                            }));
                            break;

                        default:
                            break;
                    }
                }
                if (transaction.TableEntity is StateEntity)
                {
                    var stateData = transaction.TableEntity as StateEntity;
                    switch (transaction.ActionType)
                    {
                        case TableTransactionActionType.Add:
                            multipleQuery.Add(new Tuple<string, Action<DbCommand>>(addStateSql, command =>
                            {
                                command.AddParameter(nameof(StateEntity.StateId), stateData.StateId);
                                command.AddParameter(nameof(StateEntity.SequenceId), stateData.SequenceId);
                                command.AddParameter(nameof(StateEntity.TransactionId), stateData.TransactionId);
                                command.AddParameter(nameof(StateEntity.TransactionTimestamp), stateData.TransactionTimestamp);
                                command.AddParameter(nameof(StateEntity.TransactionManager), stateData.TransactionManager);
                                command.AddParameter(nameof(StateEntity.SateData), stateData.SateData);
                                command.AddParameter(nameof(StateEntity.Timestamp), stateData.Timestamp);
                                command.AddParameter(nameof(StateEntity.ETag), stateData.ETag);
                            }));
                            break;

                        case TableTransactionActionType.UpdateReplace:
                            multipleQuery.Add(new Tuple<string, Action<DbCommand>>(updateStateSql, command =>
                            {
                                command.AddParameter(nameof(StateEntity.StateId), stateData.StateId);
                                command.AddParameter(nameof(StateEntity.SequenceId), stateData.SequenceId);
                                command.AddParameter(nameof(StateEntity.TransactionId), stateData.TransactionId);
                                command.AddParameter(nameof(StateEntity.TransactionTimestamp), stateData.TransactionTimestamp);
                                command.AddParameter(nameof(StateEntity.TransactionManager), stateData.TransactionManager);
                                command.AddParameter(nameof(StateEntity.SateData), stateData.SateData);
                                command.AddParameter(nameof(StateEntity.Timestamp), stateData.Timestamp);
                            }));
                            break;

                        case TableTransactionActionType.Delete:
                            multipleQuery.Add(new Tuple<string, Action<DbCommand>>(delStateSql, command =>
                            {
                                command.AddParameter(nameof(StateEntity.StateId), stateData.StateId);
                                command.AddParameter(nameof(StateEntity.SequenceId), stateData.SequenceId);
                                command.AddParameter(nameof(StateEntity.ETag), stateData.ETag);
                            }));
                            break;

                        default:
                            break;
                    }
                }
            }

            await storage.ExecuteTransactionAsync(multipleQuery).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
