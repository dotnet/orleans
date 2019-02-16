using System;

namespace Orleans.Runtime
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class PersistentStateAttribute : Attribute, IFacetMetadata, IPersistentStateConfiguration
    {
        public string StateName { get; }
        public string StorageName { get; }

        public PersistentStateAttribute(string stateName, string storageName = null)
        {
            this.StateName = stateName;
            this.StorageName = storageName;
        }
    }
}
