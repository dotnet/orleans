using System;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Identifies a grain constructor parameter as a transactional state facet.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransactionalStateAttribute : Attribute, IFacetMetadata, ITransactionalStateConfiguration
    {
        /// <inheritdoc />
        public string StateName { get; }

        /// <inheritdoc />
        public string? StorageName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionalStateAttribute"/> class.
        /// </summary>
        /// <param name="stateName">The transactional state name.</param>
        /// <param name="storageName">The name of the storage provider used for the transactional state.</param>
        public TransactionalStateAttribute(string stateName, string? storageName = null)
        {
            this.StateName = stateName;
            this.StorageName = storageName;
        }
    }
}
