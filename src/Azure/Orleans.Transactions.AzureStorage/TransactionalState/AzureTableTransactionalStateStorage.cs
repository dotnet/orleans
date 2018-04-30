using System;
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
        JsonSerializerSettings JsonSettings;
        private readonly ILogger logger;

        private KeyEntity key;
        private List<StateEntity> states;

        public AzureTableTransactionalStateStorage(CloudTable table, string partition, JsonSerializerSettings JsonSettings, ILogger<AzureTableTransactionalStateStorage<TState>> logger)
        {
            this.table = table;
            this.partition = partition;
            this.JsonSettings = JsonSettings;
            this.logger = logger;
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            try
            {
                Task<KeyEntity> keyTask = ReadKey();
                Task<List<StateEntity>> statesTask = ReadStates();
                this.key = await keyTask;
                this.states = await statesTask;
                if (string.IsNullOrEmpty(this.key.ETag))
                {
                    return new TransactionalStorageLoadResponse<TState>();
                }
                TState commitedState = TryFindState(this.key.CommittedTransactionId, out TState found) ? found : new TState();
                var pendingStates = states.Select(s => new PendingTransactionState<TState>(s.TransactionId, s.SequenceId, s.GetState<TState>(this.JsonSettings))).ToList();
                return new TransactionalStorageLoadResponse<TState>(this.key.ETag, commitedState, this.key.Metadata, pendingStates);
            } catch(Exception ex)
            {
                this.logger.LogError("Transactional state load failed {Exception}.", ex);
                throw;
            }
        }

        public async Task<string> Persist(string expectedETag, string metadata, List<PendingTransactionState<TState>> statesToPrepare)
        {
            try
            {
                var batchOperation = new TableBatchOperation();

                this.key.ETag = expectedETag;
                this.key.Metadata = metadata;
                if (string.IsNullOrEmpty(this.key.ETag))
                    batchOperation.Insert(this.key);
                else
                    batchOperation.Replace(this.key);

                // add new states
                List<Tuple<string,long>> stored = this.states.Select(s => Tuple.Create(s.TransactionId, s.SequenceId)).ToList();
                List<StateEntity> newStates = new List<StateEntity>();
                foreach (PendingTransactionState<TState> pendingState in statesToPrepare.Where(p => !stored.Contains(Tuple.Create(p.TransactionId, p.SequenceId))))
                {
                    var newState = StateEntity.Create(this.JsonSettings, this.partition, pendingState);
                    newStates.Add(newState);
                    batchOperation.Insert(newState);
                }

                if (batchOperation.Count > AzureTableConstants.MaxBatchSize)
                {
                    this.logger.LogError("Too many pending states. PendingStateCount {PendingStateCount}.", batchOperation.Count);
                    throw new InvalidOperationException($"Too many pending states. PendingStateCount {batchOperation.Count}");
                }

                await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);
                this.states.AddRange(newStates);
                return this.key.ETag;
            }
            catch (Exception ex)
            {
                this.logger.LogError("Transactional state persist failed {Exception}.", ex);
                throw;
            }
        }

        public async Task<string> Confirm(string expectedETag, string metadata, string transactionIdToCommit)
        {
            try
            {
                // only update storage if transaction id is greater then previously committed
                if (string.Compare(transactionIdToCommit, this.key.CommittedTransactionId) <= 0)
                    return this.key.ETag;

                if (!TryFindState(transactionIdToCommit, out TState state))
                {
                    this.logger.LogCritical("Transactional state non-recoverable error.  Attempting to confirm a transaction {TransactionId} for which no state exists.", transactionIdToCommit);
                    throw new InvalidOperationException($"Transactional state non-recoverable error.  Attempting to confirm a transaction {transactionIdToCommit} for which no state exists.");
                }

                this.key.ETag = expectedETag;
                this.key.Metadata = metadata;
                this.key.CommittedTransactionId = transactionIdToCommit;
                await WriteKey();
                var dead = this.states.Where(p => string.Compare(p.TransactionId, transactionIdToCommit) <0).ToList();
                this.states = this.states.Where(p => string.Compare(p.TransactionId, transactionIdToCommit) >= 0).ToList();
                Cleanup(dead).Ignore();
                return this.key.ETag;
            }
            catch (Exception ex)
            {
                this.logger.LogError("Transactional state confirm failed {Exception}.", ex);
                throw;
            }
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

        private Task WriteKey()
        {
            return (string.IsNullOrEmpty(this.key.ETag))
                ? this.table.ExecuteAsync(TableOperation.Insert(this.key))
                : this.table.ExecuteAsync(TableOperation.Replace(this.key));
        }

        private async Task<List<StateEntity>> ReadStates()
        {
            var query = new TableQuery<StateEntity>()
                .Where(AzureStorageUtils.RangeQuery(this.partition, StateEntity.RKMin, StateEntity.RKMax));
            TableContinuationToken continuationToken = null;
            List<StateEntity> results = new List<StateEntity>();
            do
            {
                TableQuerySegment<StateEntity> queryResult = await table.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                results.AddRange(queryResult.Results);
                continuationToken = queryResult.ContinuationToken;
            } while (continuationToken != null);
            return results;
        }

        private bool TryFindState(string transactionId, out TState state)
        {
            state = null;
            if (string.IsNullOrEmpty(transactionId))
                return false;

            StateEntity entity = this.states.FirstOrDefault(s => s.TransactionId == transactionId);
            if (entity == null)
            {
                throw new InvalidOperationException($"Transactional state non-recoverable error.  Commited state for transaction {transactionId} not found.");
            }
            state = entity.GetState<TState>(this.JsonSettings);
            return true;
        }

        private async Task Cleanup(List<StateEntity> deadStates)
        {
            try
            {
                var batchOperation = new TableBatchOperation();
                deadStates.ForEach(batchOperation.Delete);
                await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogInformation("Error cleaning up transactional states {Exception}.  Ignoring", ex);
            }
        }
    }
}
