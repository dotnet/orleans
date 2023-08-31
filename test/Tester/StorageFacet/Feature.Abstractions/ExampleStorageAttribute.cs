namespace Tester.StorageFacet.Abstractions
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class ExampleStorageAttribute : Attribute, IFacetMetadata, IExampleStorageConfig
    {
        public string StorageProviderName { get; }

        public string StateName { get; }

        public ExampleStorageAttribute(string storageProviderName = null, string stateName = null)
        {
            StorageProviderName = storageProviderName;
            StateName = stateName;
        }
    }
}
