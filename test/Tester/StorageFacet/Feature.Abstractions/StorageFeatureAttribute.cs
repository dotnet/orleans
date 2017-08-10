using System;
using Orleans;

namespace Tester.StorageFacet.Abstractions
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class StorageFeatureAttribute : FacetAttribute, IStorageFeatureConfig
    {
        public string StorageProviderName { get; }

        public string StateName { get; }

        public StorageFeatureAttribute(string storageProviderName = null, string stateName = null)
        {
            this.StorageProviderName = storageProviderName;
            this.StateName = stateName;
        }
    }

}
