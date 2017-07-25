using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Tester
{
    public class BlobStorageFacet<TState> : IStorageFacet<TState>
    {
        private readonly IStorageFacetConfig config;

        public string Name => this.config.StateName;
        public TState State { get; set; }

        public BlobStorageFacet(IStorageFacetConfig config)
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

    public class TableStorageFacet<TState> : IStorageFacet<TState>, IGrainLifecycleParticipant
    {
        private readonly IStorageFacetConfig config;
        private bool activateCalled;

        public string Name => this.config.StateName;
        public TState State { get; set; }

        public TableStorageFacet(IStorageFacetConfig config)
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
}
