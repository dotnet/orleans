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

﻿using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using System.Threading.Tasks;


namespace Orleans.Streams
{
    internal class StreamProviderManager : IStreamProviderManager
    {
        private ProviderLoader<IStreamProvider> appStreamProviders;

        internal async Task LoadStreamProviders(
            IDictionary<string, ProviderCategoryConfiguration> configs,
            IStreamProviderRuntime providerRuntime)
        {
            appStreamProviders = new ProviderLoader<IStreamProvider>();

            if (!configs.ContainsKey(ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME)) return;

            appStreamProviders.LoadProviders(configs[ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME].Providers, this);
            await appStreamProviders.InitProviders(providerRuntime);
        }

        internal async Task StartStreamProviders()
        {
            var providers = appStreamProviders.GetProviders();
            foreach (IStreamProvider streamProvider in providers)
            {
                var provider = streamProvider as IStreamProviderImpl;
                if (provider != null)
                {
                    await provider.Start();   
                }
            }
        }

        public IEnumerable<IStreamProvider> GetStreamProviders()
        {
            return appStreamProviders.GetProviders();
        }

        public IProvider GetProvider(string name)
        {
            return appStreamProviders.GetProvider(name);
        }
    }
}