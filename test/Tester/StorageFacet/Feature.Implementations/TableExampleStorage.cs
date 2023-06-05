using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

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
        private readonly IGrainContextAccessor contextAccessor;
        public TableExampleStorageFactory(IGrainContextAccessor context)
        {
            this.contextAccessor = context;
        }

        public IExampleStorage<TState> Create<TState>(IExampleStorageConfig config)
        {
            var context = this.contextAccessor.GrainContext;
            var storage = context.ActivationServices.GetRequiredService<TableExampleStorage<TState>>();
            storage.Configure(config);
            storage.Participate(context.ObservableLifecycle);
            return storage;
        }
    }

    public static class TableExampleStorageExtensions
    {
        public static void UseTableExampleStorage(this ISiloBuilder builder, string name)
        {
            builder.ConfigureServices(services =>
            {
                services.AddTransientNamedService<IExampleStorageFactory, TableExampleStorageFactory>(name);
                services.AddTransient(typeof(TableExampleStorage<>));
            });
        }
    }
}
