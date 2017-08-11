using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;
using Tester.StorageFacet.Infrastructure;

namespace Tester.StorageFacet.Implementations
{
    public class BlobStorageFeature<TState> : IStorageFeature<TState>, IConfigurableStorageFeature
    {
        private IStorageFeatureConfig config;

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

        public void Configure(IStorageFeatureConfig cfg)
        {
            this.config = cfg;
        }
    }

    public class BlobStorageFeatureFactory : StorageFeatureFactory
    {
        public BlobStorageFeatureFactory(IGrainActivationContext context) : base(typeof(BlobStorageFeature<>), context)
        {
        }
    }

    public static class BlobStorageFeatureExtensions
    {
        public static void UseBlobStorageFeature(this IServiceCollection services, string name)
        {
            services.AddTransientNamedService<IStorageFeatureFactory,BlobStorageFeatureFactory>(name);
            services.AddTransient(typeof(BlobStorageFeature<>));
        }
    }
}
