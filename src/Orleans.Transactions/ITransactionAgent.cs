using System;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// Coordinates transaction creation, resolution, and abortion for a silo.
    /// </summary>
    /// <remarks>
    /// A silo has one transaction agent.
    /// </remarks>
    public interface ITransactionAgent
    {
        /// <summary>
        /// Starts a new transaction.
        /// </summary>
        /// <param name="readOnly">A value indicating whether the transaction permits read operations only.</param>
        /// <param name="timeout">The interval after which the transaction is eligible to be aborted.</param>
        /// <returns>The new transaction's context information.</returns>
        Task<TransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout);

        /// <summary>
        /// Resolves a transaction by committing or aborting it.
        /// </summary>
        /// <param name="transactionInfo">The transaction context information.</param>
        /// <returns>
        /// The resolution status and the underlying exception, if one contributed to the outcome.
        /// </returns>
        Task<(TransactionalStatus Status, Exception? exception)> Resolve(TransactionInfo transactionInfo);

        /// <summary>
        /// Abort a transaction.
        /// </summary>
        /// <param name="transactionInfo">The transaction context information.</param>
        /// <returns>A <see cref="Task"/> representing the abort operation.</returns>
        /// <remarks>This operation completes without propagating transaction protocol exceptions.</remarks>
        Task Abort(TransactionInfo transactionInfo);
    }
}
