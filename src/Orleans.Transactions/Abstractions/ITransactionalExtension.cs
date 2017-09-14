using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions
{
    /// <summary>
    /// This is a grain extension interface that allows a grain to take part in transaction orchestration.
    /// </summary>
    public interface ITransactionalExtension : IGrainExtension
    {
        /// <summary>
        /// Perform the prepare phase of the commit protocol on a resource managed by this extension. To
        /// succeed the extension must have all the writes that were part of the transaction and is able
        /// to persist these writes to persistent storage.
        /// <param name="transactionId">Id of the transaction to prepare</param>
        /// <param name="resourceId">Id of resource to prepare for write</param>
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
        [AlwaysInterleave]
        [Transaction(TransactionOption.NotSupported)]
        Task<bool> Prepare(long transactionId, string resourceId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion);

        /// <summary>
        /// Notification of a transaction abort on a resource managed by this extension.
        /// </summary>
        /// <param name="transactionId">Id of the aborted transaction</param>
        /// <param name="resourceId">Id of resource aborted</param>
        [AlwaysInterleave]
        [Transaction(TransactionOption.NotSupported)]
        Task Abort(long transactionId, string resourceId);

        /// <summary>
        /// Performs the second phase of the commit protocol on a resource managed by this
        /// extension.
        /// </summary>
        /// <param name="transactionId">Id of the committed transaction</param>
        /// <param name="resourceId">Id of resource committed</param>
        /// <remarks>
        /// If this method returns without throwing an exception the manager is
        /// allowed to forget about the transaction. This means that the resource
        /// must durably remember that this transaction committed so that it does
        /// not query for its status.
        /// </remarks>
        [AlwaysInterleave]
        [Transaction(TransactionOption.NotSupported)]
        Task Commit(long transactionId, string resourceId);
    }
}
