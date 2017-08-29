
using System;
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

    public static class TransactionalExtensionExtensions
    {
        public static ITransactionalResource AsTransactionalResource(this ITransactionalExtension transactionalExtension, string resourceId)
        {
            return new TransactionalResourceExtensionWrapper(transactionalExtension, resourceId);
        }

        [Serializable]
        [Immutable]
        internal sealed class TransactionalResourceExtensionWrapper : ITransactionalResource
        {
            private readonly ITransactionalExtension extension;
            private readonly string resourceId;

            public TransactionalResourceExtensionWrapper(ITransactionalExtension transactionalExtension, string resourceId)
            {
                this.extension = transactionalExtension;
                this.resourceId = resourceId;
            }

            public Task<bool> Prepare(long transactionId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion)
            {
                return this.extension.Prepare(transactionId, resourceId, writeVersion, readVersion);
            }

            public Task Abort(long transactionId)
            {
                return this.extension.Abort(transactionId, resourceId);
            }

            public Task Commit(long transactionId)
            {
                return this.extension.Commit(transactionId, resourceId);
            }

            public bool Equals(ITransactionalResource other)
            {
                return Equals((object)other);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != this.GetType()) return false;
                return Equals((TransactionalResourceExtensionWrapper)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((extension?.GetHashCode() ?? 0) * 397) ^ (resourceId?.GetHashCode() ?? 0);
                }
            }

            private bool Equals(TransactionalResourceExtensionWrapper other)
            {
                return Equals(extension, other.extension) && string.Equals(resourceId, other.resourceId);
            }
        }
    }
}
