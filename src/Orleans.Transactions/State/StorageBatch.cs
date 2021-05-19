using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Events streamed to storage. 
    /// </summary>
    public interface ITransactionalStateStorageEvents<TState> where TState : class, new()
    {
        void Prepare(long sequenceNumber, Guid transactionId, DateTime timestamp, ParticipantId transactionManager, TState state);

        void Read(DateTime timestamp);

        void Cancel(long sequenceNumber);

        void Confirm(long sequenceNumber);

        void Commit(Guid transactionId, DateTime timestamp, List<ParticipantId> writeResources);

        void Collect(Guid transactionId);
    }

    /// <summary>
    /// Accumulates storage events, for submitting them to storage as a batch.
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    internal class StorageBatch<TState> : ITransactionalStateStorageEvents<TState>
        where TState : class, new()
    {
        // watermarks for commit, prepare, abort
        private long confirmUpTo;
        private long cancelAbove;
        private long cancelAboveStart;

        // prepare records
        private SortedDictionary<long, PendingTransactionState<TState>> prepares;

        // follow-up actions, to be executed after storing this batch
        private List<Action> followUpActions;
        private List<Func<Task<bool>>> storeConditions;
        
        // counters for each type of event
        private int total = 0;
        private int prepare = 0;
        private int read = 0;
        private int commit = 0;
        private int confirm = 0;
        private int collect = 0;
        private int cancel = 0;

        public TransactionalStateMetaData MetaData { get; private set; }

        public string ETag { get; set; }

        public int BatchSize => total;
        public override string ToString()
        {
            return $"batchsize={total} [{read}r {prepare}p {commit}c {confirm}cf {collect}cl {cancel}cc]";
        }

        public StorageBatch(TransactionalStateMetaData metaData, string etag, long confirmUpTo, long cancelAbove)
        {
            this.MetaData = metaData ?? throw new ArgumentNullException(nameof(metaData));
            this.ETag = etag;
            this.confirmUpTo = confirmUpTo;
            this.cancelAbove = cancelAbove;
            this.cancelAboveStart = cancelAbove;
            this.followUpActions = new List<Action>();
            this.storeConditions = new List<Func<Task<bool>>>();
            this.prepares = new SortedDictionary<long, PendingTransactionState<TState>>();
        }

        public StorageBatch(StorageBatch<TState> previous)
            : this(previous.MetaData, previous.ETag, previous.confirmUpTo, previous.cancelAbove)
        {
        }

        public StorageBatch(TransactionalStorageLoadResponse<TState> loadresponse)
            : this(loadresponse.Metadata, loadresponse.ETag, loadresponse.CommittedSequenceId, loadresponse.PendingStates.LastOrDefault()?.SequenceId ?? loadresponse.CommittedSequenceId)
        {
        }

        public async Task<string> Store(ITransactionalStateStorage<TState> storage)
        {
            List<PendingTransactionState<TState>> list = this.prepares.Values.ToList();
            return await storage.Store(ETag, this.MetaData, list,
                (confirm > 0) ? confirmUpTo : (long?)null,
                (cancelAbove < cancelAboveStart) ? cancelAbove : (long?)null);
        }

        public void RunFollowUpActions()
        {
            foreach (var action in followUpActions)
            {
                action();
            }
        }

        public void Read(DateTime timestamp)
        {
            read++;
            total++;

            if (MetaData.TimeStamp < timestamp)
            {
                MetaData.TimeStamp = timestamp;
            }
        }

        public void Prepare(long sequenceNumber, Guid transactionId, DateTime timestamp,
          ParticipantId transactionManager, TState state)
        {
            prepare++;
            total++;

            if (MetaData.TimeStamp < timestamp)
                MetaData.TimeStamp = timestamp;

            this.prepares[sequenceNumber] = new PendingTransactionState<TState>
            {
                SequenceId = sequenceNumber,
                TransactionId = transactionId.ToString(),
                TimeStamp = timestamp,
                TransactionManager = transactionManager,
                State = state
            };

            if (cancelAbove < sequenceNumber)
            {
                cancelAbove = sequenceNumber;
            }
        }

        public void Cancel(long sequenceNumber)
        {
            cancel++;
            total++;

            this.prepares.Remove(sequenceNumber);

            if (cancelAbove > sequenceNumber - 1)
            {
                cancelAbove = sequenceNumber - 1;
            }
        }

        public void Confirm(long sequenceNumber)
        {
            confirm++;
            total++;

            confirmUpTo = sequenceNumber;

            // remove all redundant prepare records that are superseded by a later confirmed state
            while (true)
            {
                long? first = this.prepares.Values.FirstOrDefault()?.SequenceId;

                if (first.HasValue && first < confirmUpTo)
                {
                    this.prepares.Remove(first.Value);
                }
                else
                {
                    break;
                }
            }
        }

        public void Commit(Guid transactionId, DateTime timestamp, List<ParticipantId> WriteParticipants)
        {
            commit++;
            total++;

            MetaData.CommitRecords.Add(transactionId, new CommitRecord()
            {
                Timestamp = timestamp,
                WriteParticipants = WriteParticipants
            });
        }

        public void Collect(Guid transactionId)
        {
            collect++;
            total++;

            MetaData.CommitRecords.Remove(transactionId);
        }

        public void FollowUpAction(Action action)
        {
            followUpActions.Add(action);
        }

        public void AddStorePreCondition(Func<Task<bool>> action)
        {
            this.storeConditions.Add(action);
        }

        public async Task<bool> CheckStorePreConditions()
        {
            if (this.storeConditions.Count == 0)
                return true;

            bool[] results = await Task.WhenAll(this.storeConditions.Select(a => a.Invoke()));
            return results.All(b => b);
        }
    }
}
