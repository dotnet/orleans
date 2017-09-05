using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.LogConsistency;

namespace Orleans.Runtime.LogConsistency
{
    internal class LogConsistencyProviderManager : ILogConsistencyProviderManager, ILogConsistencyProviderRuntime, IKeyedServiceCollection<string, ILogConsistencyProvider>
    {
        private ProviderLoader<ILogConsistencyProvider> providerLoader;
        private IProviderRuntime runtime;

        public IGrainFactory GrainFactory { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }

        public LogConsistencyProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime runtime, LoadedProviderTypeLoaders loadedProviderTypeLoaders)
        {
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
            this.runtime = runtime;
            providerLoader = new ProviderLoader<ILogConsistencyProvider>(loadedProviderTypeLoaders);
        }

        internal Task LoadLogConsistencyProviders(IDictionary<string, ProviderCategoryConfiguration> configs)
        {

            if (!configs.ContainsKey(ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME))
                return Task.CompletedTask;

            providerLoader.LoadProviders(configs[ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME].Providers, this);
            return providerLoader.InitProviders(this);
        }

        internal void UnloadLogConsistencyProviders()
        {
            foreach (var provider in providerLoader.GetProviders())
            {
                var disp = provider as IDisposable;
                if (disp != null)
                    disp.Dispose();
            }
        }

        public int GetLoadedProvidersNum()
        {
            return providerLoader.GetNumLoadedProviders();
        }

        public IList<ILogConsistencyProvider> GetProviders()
        {
            return providerLoader.GetProviders();
        }

        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
#pragma warning disable 618
            runtime.SetInvokeInterceptor(interceptor);
#pragma warning restore 618
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
#pragma warning disable 618
            return runtime.GetInvokeInterceptor();
#pragma warning restore 618
        }

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc) where TExtension : IGrainExtension where TExtensionInterface : IGrainExtension
        {
            return runtime.BindExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }

        public Logger GetLogger(string loggerName)
        {
            return LogManager.GetLogger(loggerName, LoggerType.Provider);
        }

        public Guid ServiceId
        {
            get { return runtime.ServiceId; }
        }

        public string SiloIdentity
        {
            get { return runtime.SiloIdentity; }
        }

        /// <summary>
        /// Get list of providers loaded in this silo.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> GetProviderNames()
        {
            var providers = providerLoader.GetProviders();
            return providers.Select(p => p.GetType().FullName).ToList();
        }

        public bool TryGetProvider(string name, out ILogConsistencyProvider provider, bool caseInsensitive = false)
        {
            return providerLoader.TryGetProvider(name, out provider, caseInsensitive);
        }

        public IProvider GetProvider(string name)
        {
            return providerLoader.GetProvider(name, true);
        }

        public ILogConsistencyProvider GetService(IServiceProvider services, string key)
        {
            ILogConsistencyProvider provider;
            return TryGetProvider(key, out provider) ? provider : default(ILogConsistencyProvider);
        }

        public ILogConsistencyProvider GetDefaultProvider()
        {
            try
            {
                return providerLoader.GetDefaultProvider(Constants.DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME);
            } catch(InvalidOperationException)
            {
                // default ILogConsistencyProvider are optional, will fallback to grain specific if not configured.
                return default(ILogConsistencyProvider);
            }
        }
    }
}
