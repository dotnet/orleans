
using Orleans.Serialization;
using System;

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
        string Id { get; }

        /// <summary>
        /// Record an unhandled exception thrown by a grain call. This causes the transaction to abort.
        /// </summary>
        /// <param name="e">The unhandled exception that was thrown by the grain call</param>
        /// <param name="serializationManager">The serialization manager used for serializing the exception</param>
        void RecordException(Exception e, SerializationManager serializationManager);

        /// <summary>
        /// Check if this transaction must abort. 
        /// </summary>
        /// <param name="serializationManager">The serialization manager used for deserializing the exception</param>
        /// <returns>returns an exception object if the transaction must abort, or null otherwise</returns>
        OrleansTransactionAbortedException MustAbort(SerializationManager serializationManager);

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
        /// Applies all pending joins 
        /// </summary>
        void ReconcilePending();
    }

 
}
