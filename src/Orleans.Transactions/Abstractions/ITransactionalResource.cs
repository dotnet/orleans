using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Interface that allows a component to be a transaction participant.
    /// </summary>
    public interface ITransactionalResource
    {
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
        /// One-way message sent by TA to participants to let them know a transaction has aborted.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        Task Abort(Guid transactionId);

        /// <summary>
        /// One-way message sent by TM to participants to let them know a transaction has aborted.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        /// <param name="timeStamp">The commit timestamp of the aborted transaction</param>
        /// <param name="status">Reason for abort</param>
        Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status);

        /// <summary>
        /// Request sent by TM to participants to let them know a transaction has committed.
        /// Participants respond after cleaning up all prepare records.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        /// <param name="timeStamp">The commit timestamp of the aborted transaction</param>
        Task Confirm(Guid transactionId, DateTime timeStamp);
    }
}
