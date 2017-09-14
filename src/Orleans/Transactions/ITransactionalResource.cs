
using System;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// Interface that allows a component to take part in transaction orchestration.
    /// </summary>
    public interface ITransactionalResource : IEquatable<ITransactionalResource>
    {
        /// <summary>
        /// Perform the prepare phase of the commit protocol. To succeed the resource
        /// must have all the writes that were part of the transaction and is able
        /// to persist these writes to persistent storage.
        /// <param name="transactionId">Id of the transaction to prepare</param>
        /// <param name="writeVersion">version of state to prepare for write</param>
        /// <param name="readVersion">version of state to prepare for read</param>
        /// </summary>
        /// <returns>Whether prepare was performed successfully</returns>
        /// <remarks>
        /// The resource cannot abort the transaction after it has returned true from
        /// Prepare.  However, if it can infer that the transaction will definitely
        /// be aborted (e.g., because it learns that the transaction depends on another
        /// transaction which has aborted) then it can proceed to rollback the aborted
        /// transaction.
        /// </remarks>
        Task<bool> Prepare(long transactionId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion);

        /// <summary>
        /// Notification of a transaction abort.
        /// </summary>
        /// <param name="transactionId">Id of the aborted transaction</param>
        Task Abort(long transactionId);

        /// <summary>
        /// Second phase of the commit protocol.
        /// </summary>
        /// <param name="transactionId">Id of the committed transaction</param>
        /// <remarks>
        /// If this method returns without throwing an exception the manager is
        /// allowed to forget about the transaction. This means that the resource
        /// must durably remember that this transaction committed so that it does
        /// not query for its status.
        /// </remarks>
        Task Commit(long transactionId);
    }
}
