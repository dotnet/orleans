using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans.Runtime.TestHooks
{

    /// <summary>
    /// Test hook functions for white box testing implemented as a SystemTarget
    /// </summary>
    internal class TestHooksSystemTarget : SystemTarget, ITestHooksSystemTarget
    {
        private readonly Silo silo;
        internal bool ExecuteFastKillInProcessExit;
        private ObserverSubscriptionManager<ITestHooksObserver> subsManager;
        private readonly IConsistentRingProvider consistentRingProvider;
        private ConcurrentDictionary<IPEndPoint, double> SimulatedMessageLoss;

        internal TestHooksSystemTarget(Silo s) : base(Constants.TestHooksSystemTargetId, s.SiloAddress)
        {
            silo = s;
            ExecuteFastKillInProcessExit = true;
            subsManager = new ObserverSubscriptionManager<ITestHooksObserver>();
            consistentRingProvider = CheckReturnBoundaryReference("ring provider", silo.RingProvider);
        }

        public Task<SiloAddress> GetPrimaryTargetSilo(uint key)
        {
            return Task.FromResult(consistentRingProvider.GetPrimaryTargetSilo(key));
        }

        public Task<string> GetConsistentRingProviderString()
        {
            return Task.FromResult(consistentRingProvider.ToString());
        }

        public Task<bool> HasStatisticsProvider() => Task.FromResult(silo.StatisticsProviderManager != null);

        public Task<Guid> GetServiceId() => Task.FromResult(silo.GlobalConfig.ServiceId);

        public Task<IEnumerable<string>> GetStorageProviderNames() => Task.FromResult(silo.StorageProviderManager.GetProviderNames());

        public Task<IEnumerable<string>> GetStreamProviderNames() => Task.FromResult(silo.StreamProviderManager.GetStreamProviders().Select(p => ((IProvider)p).Name).AsEnumerable());

        public Task<IEnumerable<string>> GetAllSiloProviderNames() => Task.FromResult(silo.AllSiloProviders.Select(p => ((IProvider)p).Name).AsEnumerable());

        public Task SuppressFastKillInHandleProcessExit()
        {
            ExecuteFastKillInProcessExit = false;
            return TaskDone.Done;
        }

        public Task<IDictionary<GrainId, IGrainInfo>> GetDirectoryForTypeNamesContaining(string expr)
        {
            var x = new Dictionary<GrainId, IGrainInfo>();
            foreach (var kvp in ((LocalGrainDirectory)silo.LocalGrainDirectory).DirectoryPartition.GetItems())
            {
                if (kvp.Key.IsSystemTarget || kvp.Key.IsClient || !kvp.Key.IsGrain)
                    continue;// Skip system grains, system targets and clients
                if (((Catalog)silo.Catalog).GetGrainTypeName(kvp.Key).Contains(expr))
                    x.Add(kvp.Key, kvp.Value);
            }
            return Task.FromResult(x as IDictionary<GrainId, IGrainInfo>);
        }
        
        public Task BlockSiloCommunication(IPEndPoint destination, double lost_percentage)
        {
            if (SimulatedMessageLoss == null)
                SimulatedMessageLoss = new ConcurrentDictionary<IPEndPoint, double>();

            SimulatedMessageLoss[destination] = lost_percentage;

            return TaskDone.Done;
        }

        public Task UnblockSiloCommunication()
        {
            SimulatedMessageLoss = null;
            return TaskDone.Done;
        }

        private readonly SafeRandom random = new SafeRandom();

        public Task<bool> ShouldDrop(Message msg)
        {
            var should = false;
            if (SimulatedMessageLoss != null)
            {
                double blockedpercentage;
                SimulatedMessageLoss.TryGetValue(msg.TargetSilo.Endpoint, out blockedpercentage);
                should = (random.NextDouble() * 100 < blockedpercentage);
            }

            return Task.FromResult(should);
        }

        public Task<int> UnregisterGrainForTesting(GrainId grain) => Task.FromResult(((Catalog)silo.Catalog).UnregisterGrainForTesting(grain));

        public Task SetDirectoryLazyDeregistrationDelay(TimeSpan timeSpan)
        {
            silo.OrleansConfig.Globals.DirectoryLazyDeregistrationDelay = timeSpan;
            return TaskDone.Done;
        }

        public Task SetMaxForwardCount(int val)
        {
            silo.OrleansConfig.Globals.MaxForwardCount = val;
            return TaskDone.Done;
        }

        public Task DecideToCollectActivation(GrainId grainId)
        {
            subsManager.Notify(s => s.OnCollectActivation(grainId));
            return TaskDone.Done;
        }

        public Task RegisterTestHooksObserver(ITestHooksObserver observer)
        {
            subsManager.Subscribe(observer);
            return TaskDone.Done;
        }

        public Task AddCachedAssembly(string targetAssemblyName, GeneratedAssembly cachedAssembly)
        {
            CodeGeneratorManager.AddGeneratedAssembly(targetAssemblyName, cachedAssembly);
            return TaskDone.Done;
        }

        private static T CheckReturnBoundaryReference<T>(string what, T obj) where T : class
        {
            if (obj == null) return null;
            if (
#if !NETSTANDARD_TODO
                    obj is MarshalByRefObject ||
#endif
                    obj is ISerializable)
            {
                // Referernce to the provider can safely be passed across app-domain boundary in unit test process
                return obj;
            }
            throw new InvalidOperationException(string.Format("Cannot return reference to {0} {1} if it is not MarshalByRefObject or Serializable",
                what, TypeUtils.GetFullName(obj.GetType())));
        }
    }
}
