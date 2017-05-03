using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime.ConsistentRing;
using Orleans.Storage;
using Orleans.Streams;

namespace Orleans.Runtime.TestHooks
{

    /// <summary>
    /// Test hook functions for white box testing implemented as a SystemTarget
    /// </summary>
    internal class TestHooksSystemTarget : SystemTarget, ITestHooksSystemTarget
    {
        private readonly Silo silo;
        private readonly IConsistentRingProvider consistentRingProvider;

        internal TestHooksSystemTarget(Silo silo) : base(Constants.TestHooksSystemTargetId, silo.SiloAddress)
        {
            this.silo = silo;
            consistentRingProvider = silo.RingProvider;
        }

        public Task<SiloAddress> GetConsistentRingPrimaryTargetSilo(uint key)
        {
            return Task.FromResult(consistentRingProvider.GetPrimaryTargetSilo(key));
        }

        public Task<string> GetConsistentRingProviderDiagnosticInfo()
        {
            return Task.FromResult(consistentRingProvider.ToString());
        }

        public Task<bool> HasStatisticsProvider() => Task.FromResult(silo.StatisticsProviderManager != null);

        public Task<Guid> GetServiceId() => Task.FromResult(silo.GlobalConfig.ServiceId);

        public Task<bool> HasStorageProvider(string providerName)
        {
            IStorageProvider tmp;
            return Task.FromResult(silo.StorageProviderManager.TryGetProvider(providerName, out tmp));
        }

        public Task<bool> HasStreamProvider(string providerName)
        {
            try
            {
                silo.StreamProviderManager.GetStreamProvider(providerName);
                return Task.FromResult(true);
            }
            catch (KeyNotFoundException)
            {
                return Task.FromResult(false);
            }
        }

        public Task<bool> HasBoostraperProvider(string providerName)
        {
            foreach (var provider in silo.BootstrapProviders)
            {
                if (String.Equals(providerName, provider.Name))
                {
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        public Task<ICollection<string>> GetStorageProviderNames() => Task.FromResult<ICollection<string>>(silo.StorageProviderManager.GetProviderNames().ToList());

        public Task<ICollection<string>> GetStreamProviderNames() => Task.FromResult<ICollection<string>>(silo.StreamProviderManager.GetStreamProviders().Select(p => ((IProvider)p).Name).ToList());

        public Task<ICollection<string>> GetAllSiloProviderNames() => Task.FromResult<ICollection<string>>(silo.AllSiloProviders.Select(p => p.Name).ToList());

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(((Catalog)silo.Catalog).UnregisterGrainForTesting(grain));

        public Task AddCachedAssembly(string targetAssemblyName, GeneratedAssembly cachedAssembly)
        {
            CodeGeneratorManager.AddGeneratedAssembly(targetAssemblyName, cachedAssembly);
            return Task.CompletedTask;
        }

        public Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod)
        {
            this.silo.Metrics.LatchIsOverload(overloaded);
            
            Task.Delay(latchPeriod).ContinueWith(t => UnlatchIsOverloaded()).Ignore();
            return Task.CompletedTask;
        }

        private void UnlatchIsOverloaded()
        {
            this.silo.Metrics.UnlatchIsOverloaded();
        }
    }
}
