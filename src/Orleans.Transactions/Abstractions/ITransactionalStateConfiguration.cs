
namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Configures a transactional state facet.
    /// </summary>
    public interface ITransactionalStateConfiguration
    {
        /// <summary>
        /// Gets the transactional state name.
        /// </summary>
        string StateName { get; }

        /// <summary>
        /// Gets the name of the storage provider used for the transactional state.
        /// </summary>
        string? StorageName { get; }
    }
}
