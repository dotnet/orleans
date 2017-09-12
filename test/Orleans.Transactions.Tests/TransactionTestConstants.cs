
namespace Orleans.Transactions.Tests
{
    public static class TransactionTestConstants
    {
        /// <summary>
        /// Max number of grains to include in a transaction for test purposes.  Not a hard limit of the transaction system.
        /// </summary>
        public const int MaxCoordinatedTransactions = 8;

        // grain implementations
        public const string TransactionOrchestrationGrain = "Orleans.Transactions.Tests.TransactionOrchestrationGrain";

    }
}
