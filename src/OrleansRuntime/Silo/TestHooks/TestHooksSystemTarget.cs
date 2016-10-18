using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime.ConsistentRing;

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

        public Task<ICollection<string>> GetStorageProviderNames() => Task.FromResult<ICollection<string>>(silo.StorageProviderManager.GetProviderNames().ToList());

        public Task<ICollection<string>> GetStreamProviderNames() => Task.FromResult<ICollection<string>>(silo.StreamProviderManager.GetStreamProviders().Select(p => ((IProvider)p).Name).ToList());

        public Task<ICollection<string>> GetAllSiloProviderNames() => Task.FromResult<ICollection<string>>(silo.AllSiloProviders.Select(p => p.Name).ToList());

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(((Catalog)silo.Catalog).UnregisterGrainForTesting(grain));

        public Task AddCachedAssembly(string targetAssemblyName, GeneratedAssembly cachedAssembly)
        {
            CodeGeneratorManager.AddGeneratedAssembly(targetAssemblyName, cachedAssembly);
            return TaskDone.Done;
        }

        public Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod)
        {
            this.silo.Metrics.LatchIsOverload(overloaded);
            
            Task.Delay(latchPeriod).ContinueWith(t => UnlatchIsOverloaded()).Ignore();
            return TaskDone.Done;
        }

        private void UnlatchIsOverloaded()
        {
            this.silo.Metrics.UnlatchIsOverloaded();
        }
    }
}
