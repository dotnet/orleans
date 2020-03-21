
namespace Orleans.Transactions
{
    /// <summary>
    /// Orleans Transactions error codes
    /// </summary>
    internal enum OrleansTransactionsErrorCode
    {
        /// <summary>
        /// Start of orleans transactions error codes
        /// </summary>
        OrleansTransactions = 1 << 17,
        // TODO - jbragg - add error codes for transaction errors
    }
}
