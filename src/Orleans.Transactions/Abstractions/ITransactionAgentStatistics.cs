
namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Tracks transaction agent activity.
    /// </summary>
    public interface ITransactionAgentStatistics
    {
        /// <summary>
        /// Records that a transaction has started.
        /// </summary>
        void TrackTransactionStarted();

        /// <summary>
        /// Gets the number of transactions which have started.
        /// </summary>
        long TransactionsStarted { get; }

        /// <summary>
        /// Records that a transaction has completed successfully.
        /// </summary>
        void TrackTransactionSucceeded();

        /// <summary>
        /// Gets the number of transactions which have completed successfully.
        /// </summary>
        long TransactionsSucceeded { get; }

        /// <summary>
        /// Records that a transaction has failed.
        /// </summary>
        void TrackTransactionFailed();

        /// <summary>
        /// Gets the number of transactions which have failed.
        /// </summary>
        long TransactionsFailed { get; }

        /// <summary>
        /// Records that a transaction was rejected because of transaction agent throttling.
        /// </summary>
        void TrackTransactionThrottled();

        /// <summary>
        /// Gets the number of transactions which were rejected because of transaction agent throttling.
        /// </summary>
        long TransactionsThrottled { get; }
    }
}
