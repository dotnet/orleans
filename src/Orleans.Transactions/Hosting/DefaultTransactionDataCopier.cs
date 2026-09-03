using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Copies transaction data using the Orleans serialization system.
    /// </summary>
    /// <typeparam name="TData">The transaction data type.</typeparam>
    public class DefaultTransactionDataCopier<TData> : ITransactionDataCopier<TData>
    {
        private readonly DeepCopier<TData> deepCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultTransactionDataCopier{TData}"/> class.
        /// </summary>
        /// <param name="deepCopier">The copier used to create independent copies of transaction data.</param>
        public DefaultTransactionDataCopier(DeepCopier<TData> deepCopier)
        {
            this.deepCopier = deepCopier;
        }

        /// <inheritdoc/>
        public TData DeepCopy(TData original)
        {
            return (TData)this.deepCopier.Copy(original)!;
        }
    }
}
