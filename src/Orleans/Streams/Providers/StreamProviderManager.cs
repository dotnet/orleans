using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;


namespace Orleans.Streams
{
    internal class StreamProviderManager : IStreamProviderManager
    {
        private ProviderLoader<IStreamProviderImpl> appStreamProviders;

        internal async Task LoadStreamProviders(
            IDictionary<string, ProviderCategoryConfiguration> configs,
            IStreamProviderRuntime providerRuntime)
        {
            if(appStreamProviders == null) appStreamProviders = new ProviderLoader<IStreamProviderImpl>();

            if (!configs.ContainsKey(ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME)) return;

            appStreamProviders.LoadProviders(configs[ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME].Providers, this);
            await appStreamProviders.InitProviders(providerRuntime);
        }

        internal Task StartStreamProviders()
        {
            List<Task> tasks = new List<Task>();
            var providers = appStreamProviders.GetProviders();
            foreach (IStreamProviderImpl streamProvider in providers)
            {
                var provider = streamProvider;
                tasks.Add(provider.Start());   
            }
            return Task.WhenAll(tasks);
        }

        internal Task CloseProviders()
        {
            List<Task> tasks = new List<Task>();
            foreach (IStreamProviderImpl streamProvider in appStreamProviders.GetProviders())
            {
                tasks.Add(streamProvider.Close());
            }
            return Task.WhenAll(tasks);
        }

        internal async Task StartStreamProviders(IList<string> providerNames)
        {
            List<Task> tasks = new List<Task>();
            var providers = appStreamProviders.GetProviders();
  
            foreach (IStreamProviderImpl streamProvider in providers)
            {
                if (providerNames.Contains(((IProvider)streamProvider).Name))
                {
                    var provider = streamProvider;
                    tasks.Add(provider.Start());
                }
            }
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                string exceptionMsg = string.Format("Exception starting providers: {0}", exc.Message);
                throw new ProviderStartException(exceptionMsg, exc);
            }
        }

        internal async Task RemoveProviders(IList<string> providerNames)
        {
            await appStreamProviders.RemoveProviders(providerNames);
        }

        public IEnumerable<IStreamProvider> GetStreamProviders()
        {
            return appStreamProviders.GetProviders();
        }

        public IList<IProvider> GetProviders()
        {
            return appStreamProviders.GetProviders().Cast<IProvider>().ToList();
        }

        public IProvider GetProvider(string name)
        {
            return appStreamProviders.GetProvider(name);
        }
    }
}
