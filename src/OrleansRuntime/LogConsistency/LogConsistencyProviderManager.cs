using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Providers;
using Orleans.LogConsistency;
using Orleans.Storage;

namespace Orleans.Runtime.LogConsistency
{
    internal class LogConsistencyProviderManager : ILogConsistencyProviderManager, ILogConsistencyProviderRuntime
    {
        private ProviderLoader<ILogConsistencyProvider> providerLoader;
        private IProviderRuntime runtime;

        public IGrainFactory GrainFactory { get; private set; }
        public IServiceProvider ServiceProvider { get; private set; }

        public LogConsistencyProviderManager(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime runtime)
        {
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
            this.runtime = runtime;
        }

        internal Task LoadLogConsistencyProviders(IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            providerLoader = new ProviderLoader<ILogConsistencyProvider>();

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

    }
}
