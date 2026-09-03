using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionalResourceExtension : IGrainExtension
    {
        /// <summary>Commits a read-only transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <returns>The transaction status.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("CommitReadOnly")]
        Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp);

        /// <summary>Commits a read-only transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The transaction status.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("1BB071FE")]
        Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, CancellationToken cancellationToken)
            => CommitReadOnly(resourceId, transactionId, accessCount, timeStamp);

        /// <summary>Aborts a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("Abort")]
        Task Abort(string resourceId, Guid transactionId);

        /// <summary>Aborts a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("BD051D23")]
        Task Abort(string resourceId, Guid transactionId, CancellationToken cancellationToken)
            => Abort(resourceId, transactionId);

        /// <summary>Cancels a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="status">The cancellation status.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("Cancel")]
        Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status);

        /// <summary>Cancels a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="status">The cancellation status.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("80028AB9")]
        Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status, CancellationToken cancellationToken)
            => Cancel(resourceId, transactionId, timeStamp, status);

        /// <summary>Confirms a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("Confirm")]
        Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp);

        /// <summary>Confirms a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("5DDDE6F0")]
        Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp, CancellationToken cancellationToken)
            => Confirm(resourceId, transactionId, timeStamp);

        /// <summary>Prepares a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="transactionManager">The transaction manager.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Prepare")]
        Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager);

        /// <summary>Prepares a transaction.</summary>
        /// <param name="resourceId">The transactional resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="transactionManager">The transaction manager.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("2ADCC608")]
        Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager, CancellationToken cancellationToken)
            => Prepare(resourceId, transactionId, accessCount, timeStamp, transactionManager);
    }
}
