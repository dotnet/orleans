using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;
using Tester.StorageFacet.Infrastructure;

namespace Tester.StorageFacet.Implementations
{
    public class TableStorageFeature<TState> : IStorageFeature<TState>, ILifecycleParticipant<GrainLifecyleStage>, IConfigurableStorageFeature
    {
        private IStorageFeatureConfig config;
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

        public void Participate(ILifecycleObservable<GrainLifecyleStage> lifecycle)
        {
            lifecycle.Subscribe(GrainLifecyleStage.SetupState, LoadState);
        }

        public void Configure(IStorageFeatureConfig cfg)
        {
            this.config = cfg;
        }
    }

    public class TableStorageFeatureFactory : StorageFeatureFactory
    {
        public TableStorageFeatureFactory(IGrainActivationContext context) : base(typeof(TableStorageFeature<>), context)
        {
        }
    }

    public static class TableStorageFeatureExtensions
    {
        public static void UseTableStorageFeature(this IServiceCollection services, string name)
        {
            services.AddTransientNamedService<IStorageFeatureFactory, TableStorageFeatureFactory>(name);
            services.AddTransient(typeof(TableStorageFeature<>));
        }
    }
}
