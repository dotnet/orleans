using System;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Identifies a grain constructor parameter as a transaction committer facet.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransactionCommitterAttribute : Attribute, IFacetMetadata, ITransactionCommitterConfiguration
    {
        /// <inheritdoc />
        public string ServiceName { get; }

        /// <inheritdoc />
        public string? StorageName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionCommitterAttribute"/> class.
        /// </summary>
        /// <param name="serviceName">The name of the service which receives committed operations.</param>
        /// <param name="storageName">The name of the storage provider used by the transaction committer.</param>
        public TransactionCommitterAttribute(string serviceName, string? storageName = null)
        {
            this.ServiceName = serviceName;
            this.StorageName = storageName;
        }
    }
}
