
using Orleans.Runtime;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;

namespace Tester
{
    public interface IStorageActivationService<TState>
    {
        string Name { get; }

        TState State { get; set; }

        Task SaveAsync();

        string GetExtendedInfo();
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class StorageActivationServiceAttribute : Attribute
    {
        public string StateName { get; }
        public string StorageProviderName { get; }

        public StorageActivationServiceAttribute(string storageProviderName = null, string stateName = null)
        {
            this.StorageProviderName = storageProviderName;
            this.StateName = stateName;
        }
    }

    public class AttributedStorageActivationService<TState> : IStorageActivationService<TState>
    {
        private readonly IStorageActivationService<TState> storageActivationService;

        public AttributedStorageActivationService(StorageActivationServiceFactory<TState> factory, IGrainActivationContext context)
        {
            var parameter = ActivationServiceUtilities.BindToConstructorParameter(context, typeof(IStorageActivationService<TState>));

            var attribute = parameter.GetCustomAttribute<StorageActivationServiceAttribute>();

            this.storageActivationService = factory.Create(attribute?.StorageProviderName, attribute?.StateName ?? parameter.Name);
            var observer = this.storageActivationService as ILifecycleObserver;
            if (observer != null)
            {
                context.ObservableLifeCycle.Subscribe(GrainLifecyleStage.SetupState, observer);
            }
        }

    // decorate all IStorageActivationService methods
    public string Name => storageActivationService.Name;
        public TState State { get { return storageActivationService.State; } set { storageActivationService.State = value; } }
        public Task SaveAsync() => storageActivationService.SaveAsync();
        public string GetExtendedInfo() => storageActivationService.GetExtendedInfo();
    }

    public class StorageActivationServiceFactory<TState>
    {
        public IStorageActivationService<TState> Create(string storageProviderName, string stateName)
        {
        // this would look for registered storage providers, and create the bridge (storage ActivationService) accordingly.
        // No need to know all of the storage providers up front, it's just for this prototype
        // Also these could use ActivatorUtilities or any way we want for constructing them.
        if (storageProviderName.StartsWith("Blob"))
            {
                return new BlobStorageActivationService<TState>(stateName);
            }
            else if(storageProviderName.StartsWith("Table"))
            {
                return new TableStorageActivationService<TState>(stateName);
            }

            throw new InvalidOperationException($"Provider with name {storageProviderName} not found.");
        }
    }
}
