using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;
using Orleans.Hosting;

namespace Tester.StorageFacet.Implementations
{
    public class BlobExampleStorage<TState> : IExampleStorage<TState>
    {
        private IExampleStorageConfig config;

        public string Name => this.config.StateName;
        public TState State { get; set; }

        public Task Save()
        {
            return Task.CompletedTask;
        }

        public string GetExtendedInfo()
        {
            return $"Blob:{this.Name}, StateType:{typeof(TState).Name}";
        }

        public void Configure(IExampleStorageConfig cfg)
        {
            this.config = cfg;
        }
    }

    public class BlobExampleStorageFactory : IExampleStorageFactory
    {
        private readonly IGrainActivationContext context;
        public BlobExampleStorageFactory(IGrainActivationContext context)
        {
            this.context = context;
        }

        public IExampleStorage<TState> Create<TState>(IExampleStorageConfig config)
        {
            var storage = this.context.ActivationServices.GetRequiredService<BlobExampleStorage<TState>>();
            storage.Configure(config);
            return storage;
        }
    }

    public static class BlobExampleStorageExtensions
    {
        public static void UseBlobExampleStorage(this ISiloBuilder builder, string name)
        {
            builder.ConfigureServices(services =>
            {
                services.AddTransientNamedService<IExampleStorageFactory, BlobExampleStorageFactory>(name);
                services.AddTransient(typeof(BlobExampleStorage<>));
            });
        }
    }
}
