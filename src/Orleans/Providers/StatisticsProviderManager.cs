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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Core;
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

        // used only for testing
        internal async Task LoadEmptyProviders()
        {
            statisticsProviderLoader = new ProviderLoader<IProvider>();
            statisticsProviderLoader.LoadProviders(new Dictionary<string, IProviderConfiguration>(), this);
            await statisticsProviderLoader.InitProviders(runtime);
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
            return TraceLogger.GetLogger(loggerName, TraceLogger.LoggerType.Provider);
        }

        public Guid ServiceId
        {
            get { return runtime.ServiceId; }
        }
    }
}
