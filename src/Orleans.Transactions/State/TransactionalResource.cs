using System;
using System.Threading;
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

        public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
            => await CommitReadOnly(transactionId, accessCount, timeStamp, CancellationToken.None);

        public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // validate the lock
            var (status, record) = await this.queue.RWLock.ValidateLock(transactionId, accessCount).WaitAsync(cancellationToken);
            var valid = status == TransactionalStatus.Ok;

            record.Timestamp = timeStamp;
            record.Role = CommitRole.ReadOnly;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!valid)
            {
                await this.queue.NotifyOfAbort(record, status, exception: null).WaitAsync(cancellationToken);
            }
            else
            {
                this.queue.Clock.Merge(record.Timestamp);
            }

            this.queue.RWLock.Notify();
            return await record.PromiseForTA.Task.WaitAsync(cancellationToken);
        }

        public async Task Abort(Guid transactionId)
            => await Abort(transactionId, CancellationToken.None);

        public async Task Abort(Guid transactionId, CancellationToken cancellationToken)
        {
            await this.queue.Ready(transactionId).WaitAsync(cancellationToken);
            // release the lock
            this.queue.RWLock.Rollback(transactionId);

            this.queue.RWLock.Notify();
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
            => await Cancel(transactionId, timeStamp, status, CancellationToken.None);

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status, CancellationToken cancellationToken)
        {
            await this.queue.Ready(transactionId).WaitAsync(cancellationToken);
            await this.queue.NotifyOfCancel(transactionId, timeStamp, status).WaitAsync(cancellationToken);
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
            => await Confirm(transactionId, timeStamp, CancellationToken.None);

        public async Task Confirm(Guid transactionId, DateTime timeStamp, CancellationToken cancellationToken)
        {
            await this.queue.Ready(transactionId).WaitAsync(cancellationToken);
            await this.queue.NotifyOfConfirm(transactionId, timeStamp).WaitAsync(cancellationToken);
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
            => await Prepare(transactionId, accessCount, timeStamp, transactionManager, CancellationToken.None);

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager, CancellationToken cancellationToken)
        {
            await this.queue.NotifyOfPrepare(transactionId, accessCount, timeStamp, transactionManager).WaitAsync(cancellationToken);
        }
    }
}
