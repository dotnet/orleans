using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Diagnostics;

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
            => await PrepareAndCommit(transactionId, accessCount, timeStamp, writeResources, totalResources, CancellationToken.None);

        public async Task<TransactionalStatus> PrepareAndCommit(
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            List<ParticipantId> writeResources,
            int totalResources,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // validate the lock
            var (status, record) = await this.queue.RWLock.ValidateLock(transactionId, accessCount).WaitAsync(cancellationToken);
            var valid = status == TransactionalStatus.Ok;

            record.Timestamp = timeStamp;
            record.Role = CommitRole.LocalCommit; // we are the TM
            record.WaitCount = totalResources - 1;
            record.WaitingSince = DateTime.UtcNow;
            record.WriteParticipants = writeResources;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!valid)
            {
                await this.queue.NotifyOfAbort(record, status, exception: null).WaitAsync(cancellationToken);
            }
            else
            {
                this.queue.Clock.Merge(record.Timestamp);
                if (record.WaitCount > 0)
                {
                    TransactionDiagnosticEvents.EmitTransactionManagerWaitingForPrepared(
                        this.queue.Resource,
                        transactionId,
                        timeStamp,
                        record.WaitCount,
                        record.WaitingSince + this.queue.PrepareTimeout,
                        this.queue.DiagnosticIdentity);
                }
            }

            this.queue.RWLock.Notify();
            return await record.PromiseForTA.Task.WaitAsync(cancellationToken);
        }

        public Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status)
            => Prepared(transactionId, timeStamp, resource, status, CancellationToken.None);

        public Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return this.queue.NotifyOfPrepared(transactionId, timeStamp, resource, status).WaitAsync(cancellationToken);
        }

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource)
            => await Ping(transactionId, timeStamp, resource, CancellationToken.None);

        public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource, CancellationToken cancellationToken)
        {
            await this.queue.Ready(transactionId).WaitAsync(cancellationToken);
            await this.queue.NotifyOfPing(transactionId, timeStamp, resource).WaitAsync(cancellationToken);
        }
    }
}
