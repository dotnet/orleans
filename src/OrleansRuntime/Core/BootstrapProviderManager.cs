using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Providers;

namespace Orleans.Runtime
{
    internal class BootstrapProviderManager : IProviderManager
    {
        private readonly PluginManager<IBootstrapProvider> pluginManager;
        private readonly string configCategoryName;

        internal BootstrapProviderManager()
        {
            var logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            configCategoryName = ProviderCategoryConfiguration.BOOTSTRAP_PROVIDER_CATEGORY_NAME;
            pluginManager = new PluginManager<IBootstrapProvider>(logger);
        }

        public IProvider GetProvider(string name)
        {
            return pluginManager.GetProvider(name);
        }
        public IList<IBootstrapProvider> GetProviders()
        {
            return pluginManager.GetProviders();
        }

        // Explicitly typed, for backward compat
        public async Task LoadAppBootstrapProviders(
            IDictionary<string, ProviderCategoryConfiguration> configs)
        {
            await pluginManager.LoadAndInitPluginProviders(configCategoryName, configs);
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

        private class PluginManager<T> : IProviderManager where T : class, IProvider
        {
            private readonly ProviderLoader<T> providerLoader = new ProviderLoader<T>();
            private readonly TraceLogger logger;

            internal PluginManager(TraceLogger logger)
            {
                this.logger = logger;
            }

            public IProvider GetProvider(string name)
            {
                return providerLoader != null ? providerLoader.GetProvider(name) : null;
            }

            public IList<T> GetProviders()
            {
                return providerLoader != null ? providerLoader.GetProviders() : new List<T>();
            }

            internal async Task LoadAndInitPluginProviders(
                string configCategoryName, IDictionary<string, ProviderCategoryConfiguration> configs)
            {
                ProviderCategoryConfiguration categoryConfig;
                if (!configs.TryGetValue(configCategoryName, out categoryConfig)) return;

                var providers = categoryConfig.Providers;
                providerLoader.LoadProviders(providers, this);
                logger.Info(ErrorCode.SiloCallingProviderInit, "Calling Init for {0} classes", typeof(T).Name);

                // Await here to force any errors to show this method name in stack trace, for better diagnostics
                await providerLoader.InitProviders(SiloProviderRuntime.Instance);
            }
        }
    }
}
