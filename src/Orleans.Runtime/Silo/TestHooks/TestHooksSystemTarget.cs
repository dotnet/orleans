using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime.ConsistentRing;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Counters;
using Orleans.Runtime.Storage;

namespace Orleans.Runtime.TestHooks
{
    /// <summary>
    /// Test hook functions for white box testing implemented as a SystemTarget
    /// </summary>
    internal class TestHooksSystemTarget : SystemTarget, ITestHooksSystemTarget
    {
        private readonly ISiloHost host;
        private readonly IConsistentRingProvider consistentRingProvider;

        public TestHooksSystemTarget(ISiloHost host, ILocalSiloDetails siloDetails, ILoggerFactory loggerFactory)
            : base(Constants.TestHooksSystemTargetId, siloDetails.SiloAddress, loggerFactory)
        {
            this.host = host;
            consistentRingProvider = this.host.Services.GetRequiredService<IConsistentRingProvider>();
        }

        public Task<SiloAddress> GetConsistentRingPrimaryTargetSilo(uint key)
        {
            return Task.FromResult(consistentRingProvider.GetPrimaryTargetSilo(key));
        }

        public Task<string> GetConsistentRingProviderDiagnosticInfo()
        {
            return Task.FromResult(consistentRingProvider.ToString());
        }

        public Task<bool> HasStatisticsProvider() => Task.FromResult(this.host.Services.GetService<StatisticsProviderManager>() != null);

        public Task<Guid> GetServiceId() => Task.FromResult(this.host.Services.GetRequiredService<GlobalConfiguration>().ServiceId);

        public Task<bool> HasStorageProvider(string providerName)
        {
            IStorageProvider tmp;
            return Task.FromResult(this.host.Services.GetRequiredService<StorageProviderManager>().TryGetProvider(providerName, out tmp));
        }

        public Task<bool> HasStreamProvider(string providerName)
        {
            try
            {
                this.host.Services.GetRequiredService<IStreamProviderManager>().GetStreamProvider(providerName);
                return Task.FromResult(true);
            }
            catch (KeyNotFoundException)
            {
                return Task.FromResult(false);
            }
        }

        public Task<bool> HasBoostraperProvider(string providerName)
        {
            foreach (var provider in this.host.Services.GetRequiredService<BootstrapProviderManager>().GetProviders())
            {
                if (String.Equals(providerName, provider.Name))
                {
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        public Task<ICollection<string>> GetStorageProviderNames() => Task.FromResult<ICollection<string>>(this.host.Services.GetRequiredService<StorageProviderManager>().GetProviderNames().ToList());

        public Task<ICollection<string>> GetStreamProviderNames() => Task.FromResult<ICollection<string>>(this.host.Services.GetRequiredService<IStreamProviderManager>().GetStreamProviders().Select(p => ((IProvider)p).Name).ToList());

        public Task<ICollection<string>> GetAllSiloProviderNames()
        {
            List<string> allProviders = new List<string>();

            var storageProviderManager = this.host.Services.GetRequiredService<StorageProviderManager>();
            allProviders.AddRange(storageProviderManager.GetProviderNames());

            var streamProviderManager = this.host.Services.GetRequiredService<IStreamProviderManager>();
            allProviders.AddRange(streamProviderManager.GetStreamProviders().Select(p => p.Name));

            var statisticsProviderManager = this.host.Services.GetRequiredService<StatisticsProviderManager>();
            allProviders.AddRange(statisticsProviderManager.GetProviders().Select(p => p.Name));

            var booststrampProviderManager = this.host.Services.GetRequiredService<BootstrapProviderManager>();
            allProviders.AddRange(booststrampProviderManager.GetProviders().Select(p => p.Name));

            return Task.FromResult<ICollection<string>>(allProviders);
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(this.host.Services.GetRequiredService<Catalog>().UnregisterGrainForTesting(grain));
        
        public Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod)
        {
            this.host.Services.GetRequiredService<SiloStatisticsManager>().MetricsTable.LatchIsOverload(overloaded);
            
            Task.Delay(latchPeriod).ContinueWith(t => UnlatchIsOverloaded()).Ignore();
            return Task.CompletedTask;
        }

        private void UnlatchIsOverloaded()
        {
            this.host.Services.GetRequiredService<SiloStatisticsManager>().MetricsTable.UnlatchIsOverloaded();
        }
    }
}
