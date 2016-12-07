using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.Runtime.Storage
{
    internal class StorageProviderManager : IStorageProviderManager, IStorageProviderRuntime
    {
        private readonly IProviderRuntime providerRuntime;
        private ProviderLoader<IStorageProvider> storageProviderLoader;

        public StorageProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime providerRuntime)
        {
            this.providerRuntime = providerRuntime;
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
        }

        internal Task LoadStorageProviders(IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            storageProviderLoader = new ProviderLoader<IStorageProvider>();

            if (!configs.ContainsKey(ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME))
                return TaskDone.Done;

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
            return LogManager.GetLogger(loggerName, LoggerType.Provider);
        }

        public Guid ServiceId => providerRuntime.ServiceId;

        public string SiloIdentity => providerRuntime.SiloIdentity;

        public IGrainFactory GrainFactory { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }
        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
            providerRuntime.SetInvokeInterceptor(interceptor);
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
            return providerRuntime.GetInvokeInterceptor();
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
            storageProviderLoader = new ProviderLoader<IStorageProvider>();

            storageProviderLoader.LoadProviders(new Dictionary<string, IProviderConfiguration>(), this);
            return storageProviderLoader.InitProviders(providerRuntime);
        }

        // used only for testing
        internal async Task AddAndInitProvider(string name, IStorageProvider provider, IProviderConfiguration config=null)
        {
            await provider.Init(name, this, config);
            storageProviderLoader.AddProvider(name, provider, config);
        }
    }
}
