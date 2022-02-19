
namespace Orleans.Transactions.TestKit
{
    public static class TransactionTestConstants
    {
        /// <summary>
        /// Max number of grains to include in a transaction for test purposes.  Not a hard limit of the transaction system.
        /// </summary>
        public const int MaxCoordinatedTransactions = 8;

        // storage providers
        public const string TransactionStore = "TransactionStore";

        // committer service
        public const string RemoteCommitService = "RemoteCommitService";
        
        // grain implementations
        public const string NoStateTransactionalGrain = "NoStateTransactionalGrain";
        public const string SingleStateTransactionalGrain = "SingleStateTransactionalGrain";
        public const string DoubleStateTransactionalGrain = "DoubleStateTransactionalGrain";
        public const string MaxStateTransactionalGrain = "MaxStateTransactionalGrain";
    }
}
