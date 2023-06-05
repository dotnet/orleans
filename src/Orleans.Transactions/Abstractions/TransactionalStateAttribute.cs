using System;

namespace Orleans.Transactions.Abstractions
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransactionalStateAttribute : Attribute, IFacetMetadata, ITransactionalStateConfiguration
    {
        public string StateName { get; }
        public string StorageName { get; }

        public TransactionalStateAttribute(string stateName, string storageName = null)
        {
            StateName = stateName;
            StorageName = storageName;
        }
    }
}
