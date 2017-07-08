using System;
using System.Threading.Tasks;
using Orleans;

namespace Tester
{
    public class BlobStorageActivationService<TState> : IStorageActivationService<TState>
    {
        public BlobStorageActivationService(string name)
        {
            this.Name = name;
        }

        public string Name { get; }
        public TState State { get; set; }

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

    public class TableStorageActivationService<TState> : IStorageActivationService<TState>, ILifecycleObserver
    {
        private bool activateCalled;

        public TableStorageActivationService(string name)
        {
            this.Name = name;
        }

        public string Name { get; }
        public TState State { get; set; }

        public Task SaveAsync()
        {
            Console.WriteLine($"I, {this.GetType().FullName}, did something to state with name {this.Name}");
            return Task.CompletedTask;
        }

        public string GetExtendedInfo()
        {
            return $"Table:{this.Name}-ActivateCalled:{this.activateCalled}";
        }

        public Task OnStart()
        {
            this.activateCalled = true;
            return Task.CompletedTask;
        }

        public Task OnStop()
        {
            return Task.CompletedTask;
        }
    }
}
