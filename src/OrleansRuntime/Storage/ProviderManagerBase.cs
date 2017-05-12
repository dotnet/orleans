using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.Runtime.Storage
{
    internal abstract class ProviderManagerBase<TProvider> : IProviderManagerBase<TProvider> where TProvider : IProvider
    {
        protected readonly IProviderRuntime providerRuntime;
        protected ProviderLoader<TProvider> providerLoader;

        public ProviderManagerBase(IGrainFactory grainFactory, IServiceProvider serviceProvider, IProviderRuntime providerRuntime)
        {
            this.providerRuntime = providerRuntime;
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
        }

        public abstract string ProviderCategory { get; }

        internal Task LoadProviders(IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            providerLoader = new ProviderLoader<TProvider>();

            if (!configs.ContainsKey(ProviderCategory))
                return Task.CompletedTask;

            providerLoader.LoadProviders(configs[ProviderCategory].Providers, this);
            return providerLoader.InitProviders(providerRuntime);
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
            return providerLoader.GetNumLoadedProviders();
        }

        public IList<TProvider> GetProviders()
        {
            return providerLoader.GetProviders();
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
            var providers = providerLoader.GetProviders();
            return providers.Select(p => p.GetType().FullName).ToList();
        }

        public TProvider GetDefaultProvider()
        {
            return providerLoader.GetDefaultProvider(Constants.DEFAULT_PROVIDER_NAME);
        }

        public virtual bool TryGetProvider(string name, out TProvider provider, bool caseInsensitive = false)
        {
            return providerLoader.TryGetProvider(name, out provider, caseInsensitive);
        }

        public IProvider GetProvider(string name)
        {
            return providerLoader.GetProvider(name, true);
        }


        // used only for testing
        internal Task LoadEmptyProviders()
        {
            providerLoader = new ProviderLoader<TProvider>();

            providerLoader.LoadProviders(new Dictionary<string, IProviderConfiguration>(), this);
            return providerLoader.InitProviders(providerRuntime);
        }
    }
}
