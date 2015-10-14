/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.Collections.Generic;
using System.Linq;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using System.Threading.Tasks;


namespace Orleans.Streams
{
    internal class StreamProviderManager : IStreamProviderManager
    {
        private ProviderLoader<IStreamProviderImpl> appStreamProviders;

        internal async Task LoadStreamProviders(
            IDictionary<string, ProviderCategoryConfiguration> configs,
            IStreamProviderRuntime providerRuntime)
        {
            appStreamProviders = new ProviderLoader<IStreamProviderImpl>();

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
