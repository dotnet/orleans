using System;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State
{
    internal class TransactionalResource<TState> : ITransactionalResource
               where TState : class, new()
    {
        private readonly TransactionQueue<TState> queue;

        public TransactionalResource(TransactionQueue<TState> queue)
        {
            this.queue = queue;
        }

        public Task Abort(Guid transactionId)
        {
            // release the lock
            this.queue.RWLock.Rollback(transactionId, false);

            this.queue.RWLock.Notify();

            return Task.CompletedTask; // one-way, no response
        }

        public Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            this.queue.NotifyOfCancel(transactionId, timeStamp, status);
            return Task.CompletedTask;
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            await this.queue.NotifyOfConfirm(transactionId, timeStamp);
        }

        public Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            var valid = this.queue.RWLock.ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.RemoteCommit; // we are not the TM
            record.TransactionManager = transactionManager;
            record.LastSent = null;
            record.PrepareIsPersisted = false;

            if (!valid)
            {
                this.queue.NotifyOfAbort(record, status);
            }
            else
            {
                this.queue.Clock.Merge(record.Timestamp);
            }

            this.queue.RWLock.Notify();
            return Task.CompletedTask; // one-way, no response
        }
    }
}
