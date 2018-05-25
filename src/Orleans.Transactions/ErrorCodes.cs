
namespace Orleans.Transactions
{
    /// <summary>
    /// Orleans Transactions error codes
    /// </summary>
    internal enum OrleansTransactionsErrorCode
    {
        /// <summary>
        /// Start of orlean transactions errocodes
        /// </summary>
        OrleansTransactions = 1 << 17,
        // TODO - jbragg - add error codes for transaction errors - User Story 62
    }
}
