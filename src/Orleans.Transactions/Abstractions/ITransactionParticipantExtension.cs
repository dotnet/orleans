using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// This is a grain extension interface that allows a grain to be a participant in a transaction.
    /// For documentation of the methods, see <see cref="ITransactionParticipant"/>.
    /// </summary>
    public interface ITransactionParticipantExtension : IGrainExtension
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
        Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ITransactionParticipant participant);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ITransactionParticipant transactionManager);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ITransactionParticipant> writeParticipants, int totalParticipants);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ITransactionParticipant participant, TransactionalStatus status);
    }
}
