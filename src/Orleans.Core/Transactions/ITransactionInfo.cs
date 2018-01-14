using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions
{
    /// <summary>
    /// Common interface for transaction information passed along
    /// during the distributed execution of a transaction.
    /// </summary>
    public interface ITransactionInfo
    {
        /// <summary>
        /// The transaction identifier.
        /// </summary>
        long TransactionId { get; }

        /// <summary>
        /// Indicates that the transaction has aborted.
        /// </summary>
        bool IsAborted { get; set; }

        /// <summary>
        /// Forks the transaction info, for passing a copy to a call.
        /// </summary>
        ITransactionInfo Fork();

        /// <summary>
        /// Joins the transaction info from a returning call.
        /// </summary>
        /// <param name="info"></param>
        void Join(ITransactionInfo info);


        /// <summary>
        /// Applies all pending joins, and returns true if there are no orphaned calls
        /// </summary>
        /// <returns>true if there are no orphans, false otherwise</returns>
        bool ReconcilePending(out int numberOrphans);
    }


}
