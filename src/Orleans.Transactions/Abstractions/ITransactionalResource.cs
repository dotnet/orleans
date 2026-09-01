using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Interface that allows a component to be a transaction participant.
    /// </summary>
    public interface ITransactionalResource
    {
        /// <summary>
        /// Request sent by TA to all participants of a read-only transaction (one-phase commit). 
        /// Participants respond after committing or aborting the read.
        /// </summary>
        /// <param name="transactionId">the id of the transaction to prepare</param>
        /// <param name="accessCount">number of reads/writes performed on this participant by this transaction</param>
        /// <param name="timeStamp">the commit timestamp for this transaction</param>
        /// <returns></returns>
        Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp);

        /// <summary>
        /// Commits a read-only transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses performed by the transaction.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The transaction status.</returns>
        Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, CancellationToken cancellationToken)
            => CommitReadOnly(transactionId, accessCount, timeStamp);

        /// <summary>
        /// One-way message sent by TA to all participants except TM.  
        /// </summary>
        /// <param name="transactionId">the id of the transaction to prepare</param>
        /// <param name="accessCount">number of reads/writes performed on this participant by this transaction</param>
        /// <param name="timeStamp">the commit timestamp for this transaction</param>
        /// <param name="transactionManager">the transaction manager for this transaction</param>
        /// <returns></returns>
        Task Prepare(Guid transactionId, AccessCounter accessCount,
            DateTime timeStamp, ParticipantId transactionManager);

        /// <summary>
        /// Prepares a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses performed by the transaction.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="transactionManager">The transaction manager.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager, CancellationToken cancellationToken)
            => Prepare(transactionId, accessCount, timeStamp, transactionManager);

        /// <summary>
        /// One-way message sent by TA to participants to let them know a transaction has aborted.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        Task Abort(Guid transactionId);

        /// <summary>
        /// Aborts a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Abort(Guid transactionId, CancellationToken cancellationToken) => Abort(transactionId);

        /// <summary>
        /// One-way message sent by TM to participants to let them know a transaction has aborted.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        /// <param name="timeStamp">The commit timestamp of the aborted transaction</param>
        /// <param name="status">Reason for abort</param>
        Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status);

        /// <summary>
        /// Cancels a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="status">The cancellation status.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status, CancellationToken cancellationToken)
            => Cancel(transactionId, timeStamp, status);

        /// <summary>
        /// Request sent by TM to participants to let them know a transaction has committed.
        /// Participants respond after cleaning up all prepare records.
        /// </summary>
        /// <param name="transactionId">The id of the committed transaction</param>
        /// <param name="timeStamp">The commit timestamp of the committed transaction</param>
        Task Confirm(Guid transactionId, DateTime timeStamp);

        /// <summary>
        /// Confirms a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Confirm(Guid transactionId, DateTime timeStamp, CancellationToken cancellationToken)
            => Confirm(transactionId, timeStamp);
    }
}
