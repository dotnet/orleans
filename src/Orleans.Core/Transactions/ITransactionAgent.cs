using System;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// The Transaction Agent it is used by the silo and activations to
    /// interact with the transactions system.
    /// </summary>
    /// <remarks>
    /// There is one Transaction Agent per silo.
    /// TODO: does this belong in Runtime instead?
    /// </remarks>
    public interface ITransactionAgent
    {
        /// <summary>
        /// Starts a new transaction
        /// </summary>
        /// <param name="readOnly">Whether it is a read-only transaction</param>
        /// <param name="timeout">Transaction is automatically aborted if it does not complete within this time</param>
        /// <returns>Info of the new transaction</returns>
        Task<ITransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout);

        /// <summary>
        /// Attempt to Commit a transaction. If this returns with no exceptions
        /// then the transaction is successfully committed.
        /// </summary>
        /// <param name="transactionInfo">transaction info</param>
        /// <returns>None.</returns>
        /// <remarks>
        /// The method throws OrleansTransactionInDoubtException if the outcome of the Commit cannot be determined.
        /// If any other exception is thrown then the transaction is aborted.
        /// </remarks>
        Task Commit(ITransactionInfo transactionInfo);

        /// <summary>
        /// Abort a transaction.
        /// </summary>
        /// <param name="transactionInfo"></param>
        /// <param name="reason"></param>
        /// <returns>None.</returns>
        /// <remarks>This method is exception-free</remarks>
        void Abort(ITransactionInfo transactionInfo, OrleansTransactionAbortedException reason);
    }
}
