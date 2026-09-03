using System.Threading;

namespace Orleans.Transactions
{
    /// <summary>
    /// Provides access to the transaction associated with the current asynchronous execution context.
    /// </summary>
    public static class TransactionContext
    {
        private static readonly AsyncLocal<TransactionInfo?> CurrentContext = new();

        /// <summary>
        /// Gets the current transaction information.
        /// </summary>
        /// <returns>The current transaction information, or <see langword="null"/> when no transaction is active.</returns>
        public static TransactionInfo? GetTransactionInfo() => CurrentContext.Value;

        /// <summary>
        /// Gets the identifier of the current transaction.
        /// </summary>
        /// <value>The current transaction identifier.</value>
        /// <exception cref="OrleansTransactionException">There is no transaction in the current context.</exception>
        public static string CurrentTransactionId => GetRequiredTransactionInfo().Id;

        /// <summary>
        /// Gets the current transaction information.
        /// </summary>
        /// <returns>The current transaction information.</returns>
        /// <exception cref="OrleansTransactionException">There is no transaction in the current context.</exception>
        public static TransactionInfo GetRequiredTransactionInfo() => GetTransactionInfo() ?? throw new OrleansTransactionException($"A transaction context is required for access. Did you forget a [Transaction] attribute?");

        internal static void SetTransactionInfo(TransactionInfo? info)
        {
            if (!ReferenceEquals(CurrentContext.Value, info))
            {
                CurrentContext.Value = info;
            }
        }

        internal static void Clear() => CurrentContext.Value = null;
    }
}
