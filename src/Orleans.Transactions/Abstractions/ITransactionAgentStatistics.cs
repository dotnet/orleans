
namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionAgentStatistics
    {
        void TrackTransactionStarted();
        long TransactionsStarted { get; }

        void TrackTransactionSucceeded();
        long TransactionsSucceeded { get; }

        void TrackTransactionFailed();
        long TransactionsFailed { get; }

        void TrackTransactionThrottled();
        long TransactionsThrottled { get; }
    }
}
