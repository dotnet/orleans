namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Creates transaction committer facets for grain activations.
    /// </summary>
    public interface ITransactionCommitterFactory
    {
        /// <summary>
        /// Creates a transaction committer using the specified configuration.
        /// </summary>
        /// <typeparam name="TService">The service type.</typeparam>
        /// <param name="config">The transaction committer configuration.</param>
        /// <returns>The configured transaction committer.</returns>
        ITransactionCommitter<TService> Create<TService>(ITransactionCommitterConfiguration config) where TService : class;
    }
}
