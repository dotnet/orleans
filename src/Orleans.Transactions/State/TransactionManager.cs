using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State
{
    internal class TransactionManager<TState> : ITransactionManager
               where TState : class, new()
    {
        private readonly TransactionQueue<TState> queue;

        public TransactionManager(TransactionQueue<TState> queue)
        {
            this.queue = queue;
        }

        public Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            // validate the lock
            var valid = this.queue.RWLock.ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.ReadOnly;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>();

            if (!valid)
            {
                this.queue.NotifyOfAbort(record, status);
            }
            else
            {
                this.queue.Clock.Merge(record.Timestamp);
            }

            this.queue.RWLock.Notify();
            return record.PromiseForTA.Task;
        }

        public Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ITransactionParticipant> writeParticipants, int totalParticipants)
        {
            // validate the lock
            var valid = this.queue.RWLock.ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.LocalCommit; // we are the TM
            record.WaitCount = totalParticipants - 1;
            record.WaitingSince = DateTime.UtcNow;
            record.WriteParticipants = writeParticipants;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>();

            if (!valid)
            {
                this.queue.NotifyOfAbort(record, status);
            }
            else
            {
                this.queue.Clock.Merge(record.Timestamp);
            }

            this.queue.RWLock.Notify();
            return record.PromiseForTA.Task;
        }

        public Task Prepared(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant, TransactionalStatus status)
        {
            this.queue.NotifyOfPrepared(transactionId, timeStamp, status);
            return Task.CompletedTask;
        }

        public Task Ping(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant)
        {
            this.queue.NotifyOfPing(transactionId, timeStamp, participant);
            return Task.CompletedTask;
        }
    }
}
