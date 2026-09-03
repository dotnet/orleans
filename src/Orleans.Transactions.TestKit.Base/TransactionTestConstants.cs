
namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Provides provider and grain implementation names used by the transaction test kit.
    /// </summary>
    public static class TransactionTestConstants
    {
        /// <summary>
        /// The maximum number of grains included in coordinated transaction test scenarios.
        /// </summary>
        public const int MaxCoordinatedTransactions = 8;

        // storage providers
        /// <summary>
        /// The name of the transactional state storage provider used by test grains.
        /// </summary>
        public const string TransactionStore = "TransactionStore";

        // committer service
        /// <summary>
        /// The name of the remote commit service used by transaction committer tests.
        /// </summary>
        public const string RemoteCommitService = "RemoteCommitService";

        // grain implementations
        /// <summary>
        /// The class name of the transaction test grain without transactional state.
        /// </summary>
        public const string NoStateTransactionalGrain = "NoStateTransactionalGrain";

        /// <summary>
        /// The class name of the transaction test grain with one transactional state.
        /// </summary>
        public const string SingleStateTransactionalGrain = "SingleStateTransactionalGrain";

        /// <summary>
        /// The class name of the transaction test grain with two transactional states.
        /// </summary>
        public const string DoubleStateTransactionalGrain = "DoubleStateTransactionalGrain";

        /// <summary>
        /// The class name of the transaction test grain with the maximum test-kit state count.
        /// </summary>
        public const string MaxStateTransactionalGrain = "MaxStateTransactionalGrain";

        /// <summary>
        /// The class name of the transaction test grain used to verify exclusive locking.
        /// </summary>
        public const string ExclusiveLockTransactionTestGrain = "ExclusiveLockTransactionTestGrain";
    }
}
