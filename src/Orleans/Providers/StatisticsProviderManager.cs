using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Providers
{
    internal class StatisticsProviderManager : IProviderManager, IProviderRuntime
    {
        private ProviderLoader<IProvider> statisticsProviderLoader;
        private readonly string providerKind;
        private readonly IProviderRuntime runtime;

        public StatisticsProviderManager(string kind, IProviderRuntime runtime)
        {
            providerKind = kind;
            this.runtime = runtime;
        }

        public IGrainFactory GrainFactory { get { return runtime.GrainFactory; }}
        public IServiceProvider ServiceProvider { get { return runtime.ServiceProvider; } }
        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
            runtime.SetInvokeInterceptor(interceptor);
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
            return runtime.GetInvokeInterceptor();
        }

        public async Task<string> LoadProvider(IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            statisticsProviderLoader = new ProviderLoader<IProvider>();

            if (!configs.ContainsKey(providerKind))
                return null;

            var statsProviders = configs[providerKind].Providers;
            if (statsProviders.Count == 0)
            {
                return null;
            }
            if (statsProviders.Count > 1)
            {
                throw new ArgumentOutOfRangeException(providerKind + "Providers",
                    string.Format("Only a single {0} provider is supported.", providerKind));
            }
            statisticsProviderLoader.LoadProviders(statsProviders, this);
            await statisticsProviderLoader.InitProviders(runtime);
            return statisticsProviderLoader.GetProviders().First().Name;
        }

        public IProvider GetProvider(string name)
        {
            return statisticsProviderLoader.GetProvider(name, true);
        }

        public IList<IProvider> GetProviders()
        {
            return statisticsProviderLoader.GetProviders();
        }

        // used only for testing
        internal async Task LoadEmptyProviders()
        {
            statisticsProviderLoader = new ProviderLoader<IProvider>();
            statisticsProviderLoader.LoadProviders(new Dictionary<string, IProviderConfiguration>(), this);
            await statisticsProviderLoader.InitProviders(runtime);
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

        // used only for testing
        internal async Task AddAndInitProvider(string name, IProvider provider, IProviderConfiguration config = null)
        {
            if (provider != null)
            {
                await provider.Init(name, this, config);
                statisticsProviderLoader.AddProvider(name, provider, config);
            }
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
    }
}
