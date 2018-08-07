using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionalResourceExtension : IGrainExtension
    {
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        Task Abort(string resourceId, Guid transactionId);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager);
    }
}
