using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Providers
{
    internal class StatisticsProviderManager : IProviderManager, IProviderRuntime
    {
        private ProviderLoader<IProvider> statisticsProviderLoader;
        private readonly string providerKind;
        private readonly IProviderRuntime runtime;
        private readonly ILoggerFactory loggerFactory;
        public StatisticsProviderManager(IProviderRuntime runtime, LoadedProviderTypeLoaders loadedProviderTypeLoaders, ILoggerFactory loggerFactory)
        {
            providerKind = ProviderCategoryConfiguration.STATISTICS_PROVIDER_CATEGORY_NAME;
            this.runtime = runtime;
            this.loggerFactory = loggerFactory;
            statisticsProviderLoader = new ProviderLoader<IProvider>(loadedProviderTypeLoaders, loggerFactory);
        }

        public IGrainFactory GrainFactory { get { return runtime.GrainFactory; }}
        public IServiceProvider ServiceProvider { get { return runtime.ServiceProvider; } }
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

        public async Task<string> LoadProvider(IDictionary<string, ProviderCategoryConfiguration> configs)
        {
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
            return new LoggerWrapper(loggerName, this.loggerFactory);
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
