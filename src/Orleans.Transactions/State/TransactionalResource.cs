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

        public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            // validate the lock
            var (status, record) = await queue.RWLock.ValidateLock(transactionId, accessCount);
            var valid = status == TransactionalStatus.Ok;

            record.Timestamp = timeStamp;
            record.Role = CommitRole.ReadOnly;
            record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!valid)
            {
                await queue.NotifyOfAbort(record, status, exception: null);
            }
            else
            {
                queue.Clock.Merge(record.Timestamp);
            }

            queue.RWLock.Notify();
            return await record.PromiseForTA.Task;
        }

        public async Task Abort(Guid transactionId)
        {
            await queue.Ready();
            // release the lock
            queue.RWLock.Rollback(transactionId);

            queue.RWLock.Notify();
        }

        public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            await queue.Ready();
            await queue.NotifyOfCancel(transactionId, timeStamp, status);
        }

        public async Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            await queue.Ready();
            await queue.NotifyOfConfirm(transactionId, timeStamp);
        }

        public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager) => await queue.NotifyOfPrepare(transactionId, accessCount, timeStamp, transactionManager);
    }
}
