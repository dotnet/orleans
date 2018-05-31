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
        /// Attempt to Commit a transaction. 
        /// </summary>
        /// <param name="transactionInfo">transaction info</param>
        /// <returns>null if the transaction committed successfully, or an exception otherwise.
        /// If the exception is OrleansTransactionInDoubtException, it means the outcome of the Commit cannot be determined; otherwise,
        /// the transaction is guaranteed to not have taken effect.</returns>
        Task<TransactionalStatus> Commit(ITransactionInfo transactionInfo);

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
