using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Tester
{
    public class BlobStorageFeature<TState> : IStorageFeature<TState>
    {
        private readonly IStorageFeatureConfig config;

        public string Name => this.config.StateName;
        public TState State { get; set; }

        public BlobStorageFeature(IStorageFeatureConfig config)
        {
            this.config = config;
        }

        public Task SaveAsync()
        {
            Console.WriteLine($"I, {this.GetType().FullName}, did something to state with name {this.Name}");
            return Task.CompletedTask;
        }

        public string GetExtendedInfo()
        {
            return $"Blob:{this.Name}";
        }
    }

    public class TableStorageFeature<TState> : IStorageFeature<TState>, IGrainLifecycleParticipant
    {
        private readonly IStorageFeatureConfig config;
        private bool activateCalled;

        public string Name => this.config.StateName;
        public TState State { get; set; }

        public TableStorageFeature(IStorageFeatureConfig config)
        {
            this.config = config;
        }

        public Task SaveAsync()
        {
            Console.WriteLine($"I, {this.GetType().FullName}, did something to state with name {this.Name}");
            return Task.CompletedTask;
        }

        public string GetExtendedInfo()
        {
            return $"Table:{this.Name}-ActivateCalled:{this.activateCalled}";
        }

        public Task LoadState()
        {
            this.activateCalled = true;
            return Task.CompletedTask;
        }

        public void Participate(IGrainLifeCycle lifecycle)
        {
            lifecycle.Subscribe(GrainLifecyleStage.SetupState, LoadState);
        }
    }

    public class StorageFeatureFactory<TState> : IStorageFeatureFactory<TState>
    {
        public object Create(IStorageFeatureConfig config)
        {
            if (config.StorageProviderName.StartsWith("Blob"))
            {
                return new BlobStorageFeature<TState>(config);
            }
            if (config.StorageProviderName.StartsWith("Table"))
            {
                return new TableStorageFeature<TState>(config);
            }

            throw new InvalidOperationException($"Provider with name {config.StorageProviderName} not found.");
        }
    }
}
