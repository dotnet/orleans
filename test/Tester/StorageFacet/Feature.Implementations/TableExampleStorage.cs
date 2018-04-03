using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;
using Orleans.Hosting;

namespace Tester.StorageFacet.Implementations
{
    public class TableExampleStorage<TState> : IExampleStorage<TState>, ILifecycleParticipant<IGrainLifecycle>
    {
        private IExampleStorageConfig config;
        private bool activateCalled;

        public string Name => this.config.StateName;
        public TState State { get; set; }

        public Task Save()
        {
            return Task.CompletedTask;
        }

        public string GetExtendedInfo()
        {
            return $"Table:{this.Name}-ActivateCalled:{this.activateCalled}, StateType:{typeof(TState).Name}";
        }

        public Task LoadState(CancellationToken ct)
        {
            this.activateCalled = true;
            return Task.CompletedTask;
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<TableExampleStorage<TState>>(this.Name), GrainLifecycleStage.SetupState, LoadState);
        }

        public void Configure(IExampleStorageConfig cfg)
        {
            this.config = cfg;
        }
    }

    public class TableExampleStorageFactory : IExampleStorageFactory
    {
        private readonly IGrainActivationContext context;
        public TableExampleStorageFactory(IGrainActivationContext context)
        {
            this.context = context;
        }

        public IExampleStorage<TState> Create<TState>(IExampleStorageConfig config)
        {
            var storage = this.context.ActivationServices.GetRequiredService<TableExampleStorage<TState>>();
            storage.Configure(config);
            storage.Participate(this.context.ObservableLifecycle);
            return storage;
        }
    }

    public static class TableExampleStorageExtensions
    {
        public static void UseTableExampleStorage(this ISiloHostBuilder builder, string name)
        {
            builder.ConfigureServices(services =>
            {
                services.AddTransientNamedService<IExampleStorageFactory, TableExampleStorageFactory>(name);
                services.AddTransient(typeof(TableExampleStorage<>));
            });
        }
    }
}
