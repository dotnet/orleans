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
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        }

        public async Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources)
        {
            await this.queue.Ready();
            // validate the lock
            var valid = this.queue.RWLock.ValidateLock(transactionId, accessCount, out var status, out var record);

            record.Timestamp = timeStamp;
            record.Role = CommitRole.LocalCommit; // we are the TM
            record.WaitCount = totalResources - 1;
            record.WaitingSince = DateTime.UtcNow;
            record.WriteParticipants = writeResources;
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
            return await record.PromiseForTA.Task;
        }

        public async Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status)
        {
            await this.queue.Ready();
            this.queue.NotifyOfPrepared(transactionId, timeStamp, status);
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource)
        {
            await this.queue.Ready();
            this.queue.NotifyOfPing(transactionId, timeStamp, resource);
        }
    }
}
