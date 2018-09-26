﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans.Concurrency;
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

        public StorageBatch(TransactionalStorageLoadResponse<TState> loadresponse)
        {
            MetaData = loadresponse.Metadata;
            ETag = loadresponse.ETag;
            confirmUpTo = loadresponse.CommittedSequenceId;
            cancelAbove = loadresponse.PendingStates.LastOrDefault()?.SequenceId ?? loadresponse.CommittedSequenceId;
            cancelAboveStart = cancelAbove;
        }

        public StorageBatch(StorageBatch<TState> previous)
        {
            MetaData = previous.MetaData;
            confirmUpTo = previous.confirmUpTo;
            cancelAbove = previous.cancelAbove;
            cancelAboveStart = cancelAbove;
        }

        public async Task<string> Store(ITransactionalStateStorage<TState> storage)
        {
            var list = new List<PendingTransactionState<TState>>();

            if (prepares != null)
            {
                foreach (var kvp in prepares)
                {
                    list.Add(kvp.Value);
                }
            }

            return await storage.Store(ETag, MetaData, list,
                (confirm > 0) ? confirmUpTo : (long?)null,
                (cancelAbove < cancelAboveStart) ? cancelAbove : (long?)null);
        }

        public void RunFollowUpActions()
        {
            if (followUpActions != null)
            {
                foreach (var action in followUpActions)
                {
                    action();
                }
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

            if (prepares == null)
                prepares = new SortedDictionary<long, PendingTransactionState<TState>>();

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
            total++;

            if (prepares != null)
            {
                prepares.Remove(sequenceNumber);
            }

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
                long? first = prepares?.FirstOrDefault().Value?.SequenceId;

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
            if (followUpActions == null)
            {
                followUpActions = new List<Action>();
            }
            followUpActions.Add(action);
        }

        public void AddStorePreCondition(Func<Task<bool>> action)
        {
            if (this.storeConditions == null)
            {
                this.storeConditions = new List<Func<Task<bool>>>();
            }
            this.storeConditions.Add(action);
        }

        public async Task<bool> CheckStorePreConditions()
        {
            if (this.storeConditions != null && this.storeConditions.Count != 0)
            {
                bool[] results = await Task.WhenAll(this.storeConditions.Select(a => a.Invoke()));
                return results.All(b => b);
            }
            return true;
        }
    }
}
