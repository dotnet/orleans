using System;

namespace Orleans.Transactions.Abstractions
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransactionCommitterAttribute : Attribute, IFacetMetadata, ITransactionCommitterConfiguration
    {
        public string ServiceName { get; }
        public string StorageName { get; }

        public TransactionCommitterAttribute(string serviceName, string storageName = null)
        {
            ServiceName = serviceName;
            StorageName = storageName;
        }
    }
}
