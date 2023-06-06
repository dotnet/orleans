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
        private readonly long cancelAboveStart;

        // prepare records
        private readonly SortedDictionary<long, PendingTransactionState<TState>> prepares;

        // follow-up actions, to be executed after storing this batch
        private readonly List<Action> followUpActions;
        private readonly List<Func<Task<bool>>> storeConditions;
        private int prepare = 0;
        private int read = 0;
        private int commit = 0;
        private int confirm = 0;
        private int collect = 0;
        private int cancel = 0;

        public TransactionalStateMetaData MetaData { get; private set; }

        public string ETag { get; set; }

        public int BatchSize { get; private set; } = 0;
        public override string ToString() => $"batchsize={BatchSize} [{read}r {prepare}p {commit}c {confirm}cf {collect}cl {cancel}cc]";

        public StorageBatch(TransactionalStateMetaData metaData, string etag, long confirmUpTo, long cancelAbove)
        {
            MetaData = metaData ?? throw new ArgumentNullException(nameof(metaData));
            ETag = etag;
            this.confirmUpTo = confirmUpTo;
            this.cancelAbove = cancelAbove;
            cancelAboveStart = cancelAbove;
            followUpActions = new List<Action>();
            storeConditions = new List<Func<Task<bool>>>();
            prepares = new SortedDictionary<long, PendingTransactionState<TState>>();
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
            var list = prepares.Values.ToList();
            return await storage.Store(ETag, MetaData, list,
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
            BatchSize++;

            if (MetaData.TimeStamp < timestamp)
            {
                MetaData.TimeStamp = timestamp;
            }
        }

        public void Prepare(long sequenceNumber, Guid transactionId, DateTime timestamp,
          ParticipantId transactionManager, TState state)
        {
            prepare++;
            BatchSize++;

            if (MetaData.TimeStamp < timestamp)
                MetaData.TimeStamp = timestamp;

            prepares[sequenceNumber] = new PendingTransactionState<TState>
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
            BatchSize++;

            prepares.Remove(sequenceNumber);

            if (cancelAbove > sequenceNumber - 1)
            {
                cancelAbove = sequenceNumber - 1;
            }
        }

        public void Confirm(long sequenceNumber)
        {
            confirm++;
            BatchSize++;

            confirmUpTo = sequenceNumber;

            // remove all redundant prepare records that are superseded by a later confirmed state
            while (true)
            {
                var first = prepares.Values.FirstOrDefault()?.SequenceId;

                if (first.HasValue && first < confirmUpTo)
                {
                    prepares.Remove(first.Value);
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
            BatchSize++;

            MetaData.CommitRecords.Add(transactionId, new CommitRecord()
            {
                Timestamp = timestamp,
                WriteParticipants = WriteParticipants
            });
        }

        public void Collect(Guid transactionId)
        {
            collect++;
            BatchSize++;

            MetaData.CommitRecords.Remove(transactionId);
        }

        public void FollowUpAction(Action action) => followUpActions.Add(action);

        public void AddStorePreCondition(Func<Task<bool>> action) => storeConditions.Add(action);

        public async Task<bool> CheckStorePreConditions()
        {
            if (storeConditions.Count == 0)
                return true;

            var results = await Task.WhenAll(storeConditions.Select(a => a.Invoke()));
            return results.All(b => b);
        }
    }
}
