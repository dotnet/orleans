
namespace Orleans.Transactions.Tests
{
    public static class TransactionTestConstants
    {
        /// <summary>
        /// Max number of grains to include in a transaction for test purposes.  Not a hard limit of the transaction system.
        /// </summary>
        public const int MaxCoordinatedTransactions = 8;

        // storage providers
        public const string TransactionStore = "TransactionStore";

        // Transaction orchestration grains
        public const string TransactionOrchestrationGrain = "Orleans.Transactions.Tests.TransactionOrchestrationGrain";

        public enum TransactionGrainStates
        {
            SingleStateTransaction,
            DoubleStateTransaction,
            MaxStateTransaction
        }

        // grain implementations singleton TM
        public const string SingleStateTransactionalGrain = "Orleans.Transactions.Tests.SingleStateTransactionalGrain";
        public const string DoubleStateTransactionalGrain = "Orleans.Transactions.Tests.DoubleStateTransactionalGrain";
        public const string MaxStateTransactionalGrain = "Orleans.Transactions.Tests.MaxStateTransactionalGrain";

        // grain implementations using distributed TM
        public const string SingleStateTransactionalGrainDistributedTM = "Orleans.Transactions.Tests.DistributedTM.SingleStateTransactionalGrain";
        public const string DoubleStateTransactionalGrainDistributedTM = "Orleans.Transactions.Tests.DistributedTM.DoubleStateTransactionalGrain";
        public const string MaxStateTransactionalGrainDistributedTM = "Orleans.Transactions.Tests.DistributedTM.MaxStateTransactionalGrain";

    }
}
