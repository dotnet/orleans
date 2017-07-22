
using Orleans.Runtime;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;

namespace Tester
{
    public interface IStorageFacet<TState>
    {
        string Name { get; }

        TState State { get; set; }

        Task SaveAsync();

        string GetExtendedInfo();
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class StorageFacetAttribute : Attribute
    {
        public string StateName { get; }
        public string StorageProviderName { get; }

        public StorageFacetAttribute(string storageProviderName = null, string stateName = null)
        {
            this.StorageProviderName = storageProviderName;
            this.StateName = stateName;
        }
    }

    public class AttributedStorageFacet<TState> : IStorageFacet<TState>
    {
        private readonly IStorageFacet<TState> storageFacet;

        public AttributedStorageFacet(StorageFacetFactory<TState> factory, IGrainActivationContext context)
        {
            ParameterInfo parameter = context.BindToConstructorParameter<IStorageFacet<TState>>();
            
            var attribute = parameter.GetCustomAttribute<StorageFacetAttribute>();

            this.storageFacet = factory.Create(attribute?.StorageProviderName, attribute?.StateName ?? parameter.Name);
            var observer = this.storageFacet as ILifecycleObserver;
            if (observer != null)
            {
                context.ObservableLifeCycle.Subscribe(GrainLifecyleStage.SetupState, observer);
            }
        }

    // decorate all IStorageFacet methods
    public string Name => storageFacet.Name;
        public TState State { get { return storageFacet.State; } set { storageFacet.State = value; } }
        public Task SaveAsync() => storageFacet.SaveAsync();
        public string GetExtendedInfo() => storageFacet.GetExtendedInfo();
    }

    public class StorageFacetFactory<TState>
    {
        public IStorageFacet<TState> Create(string storageProviderName, string stateName)
        {
        // this would look for registered storage providers, and create the bridge (storage ActivationService) accordingly.
        // No need to know all of the storage providers up front, it's just for this prototype
        // Also these could use ActivatorUtilities or any way we want for constructing them.
        if (storageProviderName.StartsWith("Blob"))
            {
                return new BlobStorageFacet<TState>(stateName);
            }
            else if(storageProviderName.StartsWith("Table"))
            {
                return new TableStorageFacet<TState>(stateName);
            }

            throw new InvalidOperationException($"Provider with name {storageProviderName} not found.");
        }
    }
}
