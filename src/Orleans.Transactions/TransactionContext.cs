using System.Threading;

namespace Orleans.Transactions
{
    public static class TransactionContext
    {
        private static readonly AsyncLocal<TransactionInfo> CurrentContext = new();

        public static TransactionInfo GetTransactionInfo() => CurrentContext.Value;

        public static string CurrentTransactionId => GetRequiredTransactionInfo().Id;

        public static TransactionInfo GetRequiredTransactionInfo() => GetTransactionInfo() ?? throw new OrleansTransactionException($"A transaction context is required for access. Did you forget a [Transaction] attribute?");

        internal static void SetTransactionInfo(TransactionInfo info)
        {
            if (!ReferenceEquals(CurrentContext.Value, info))
            {
                CurrentContext.Value = info;
            }
        }

        internal static void Clear() => CurrentContext.Value = null;
    }
}
