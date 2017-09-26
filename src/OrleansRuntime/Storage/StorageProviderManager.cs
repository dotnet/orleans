using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.Runtime.Storage
{
    internal class StorageProviderManager : IStorageProviderManager, IStorageProviderRuntime, IKeyedServiceCollection<string,IStorageProvider>
    {
        private readonly IProviderRuntime providerRuntime;
        private ProviderLoader<IStorageProvider> storageProviderLoader;
        private readonly ILoggerFactory loggerFactory;
        public StorageProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime providerRuntime, LoadedProviderTypeLoaders loadedProviderTypeLoaders, ILoggerFactory loggerFactory)
        {
            this.providerRuntime = providerRuntime;
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
            storageProviderLoader = new ProviderLoader<IStorageProvider>(loadedProviderTypeLoaders, loggerFactory);
            this.loggerFactory = loggerFactory;
        }

        internal Task LoadStorageProviders(IDictionary<string, ProviderCategoryConfiguration> configs)
        {

            if (!configs.ContainsKey(ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME))
                return Task.CompletedTask;

            storageProviderLoader.LoadProviders(configs[ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME].Providers, this);
            return storageProviderLoader.InitProviders(providerRuntime);
        }

        public Task CloseProviders()
        {
            List<Task> tasks = new List<Task>();
            foreach (var provider in GetProviders())
            {
                tasks.Add(provider.Close());
            }
            return Task.WhenAll(tasks);
        }

        public int GetNumLoadedProviders()
        {
            return storageProviderLoader.GetNumLoadedProviders();
        }

        public IList<IStorageProvider> GetProviders()
        {
            return storageProviderLoader.GetProviders();
        }

        public Logger GetLogger(string loggerName)
        {
            return new LoggerWrapper(loggerName, loggerFactory);
        }

        public Guid ServiceId => providerRuntime.ServiceId;

        public string SiloIdentity => providerRuntime.SiloIdentity;

        public IGrainFactory GrainFactory { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }
        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
#pragma warning disable 618
            providerRuntime.SetInvokeInterceptor(interceptor);
#pragma warning restore 618
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
#pragma warning disable 618
            return providerRuntime.GetInvokeInterceptor();
#pragma warning restore 618
        }

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc) where TExtension : IGrainExtension where TExtensionInterface : IGrainExtension
        {
            return providerRuntime.BindExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }

        /// <summary>
        /// Get list of providers loaded in this silo.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> GetProviderNames()
        {
            var providers = storageProviderLoader.GetProviders();
            return providers.Select(p => p.GetType().FullName).ToList();
        }

        public IStorageProvider GetDefaultProvider()
        {
            return storageProviderLoader.GetDefaultProvider(Constants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        public bool TryGetProvider(string name, out IStorageProvider provider, bool caseInsensitive = false)
        {
            return storageProviderLoader.TryGetProvider(name, out provider, caseInsensitive);
        }

        public IProvider GetProvider(string name)
        {
            return storageProviderLoader.GetProvider(name, true);
        }

        // used only for testing
        internal Task LoadEmptyStorageProviders()
        {
            storageProviderLoader.LoadProviders(new Dictionary<string, IProviderConfiguration>(), this);
            return storageProviderLoader.InitProviders(providerRuntime);
        }

        // used only for testing
        internal async Task AddAndInitProvider(string name, IStorageProvider provider, IProviderConfiguration config=null)
        {
            await provider.Init(name, this, config);
            storageProviderLoader.AddProvider(name, provider, config);
        }

        public IStorageProvider GetService(IServiceProvider services, string key)
        {
            IStorageProvider provider;
            return TryGetProvider(key, out provider) ? provider : default(IStorageProvider);
        }
    }
}
