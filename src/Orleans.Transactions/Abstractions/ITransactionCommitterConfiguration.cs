
namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Configures a transaction committer facet.
    /// </summary>
    public interface ITransactionCommitterConfiguration
    {
        /// <summary>
        /// Gets the name of the service which receives committed operations.
        /// </summary>
        string ServiceName { get; }

        /// <summary>
        /// Gets the name of the storage provider used by the transaction committer.
        /// </summary>
        string? StorageName { get; }
    }
}
