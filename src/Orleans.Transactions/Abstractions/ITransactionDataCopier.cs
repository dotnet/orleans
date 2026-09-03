
namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Creates independent copies of data used by transactions.
    /// </summary>
    /// <typeparam name="TData">The data type.</typeparam>
    public interface ITransactionDataCopier<TData>
    {
        /// <summary>
        /// Creates a deep copy of the specified value.
        /// </summary>
        /// <param name="original">The value to copy.</param>
        /// <returns>An independent copy of <paramref name="original"/>.</returns>
        TData DeepCopy(TData original);
    }
}
