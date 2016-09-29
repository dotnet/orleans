using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal abstract class PlacementTestGrainBase : Grain
    {
        public Task<IPEndPoint> GetEndpoint()
        {
            return Task.FromResult(Data.Address.Silo.Endpoint);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

        public Task Nop()
        {
            return TaskDone.Done;
        }

        public Task StartLocalGrains(List<Guid> keys)
        {
            // we call Nop() on the grain references to ensure that they're instantiated before the promise is delivered.
            var grains = keys.Select(i => GrainFactory.GetGrain<ILocalPlacementTestGrain>(i));
            var promises = grains.Select(g => g.Nop());
            return Task.WhenAll(promises);
        }

        public async Task<Guid> StartPreferLocalGrain(Guid key)
        {
            // we call Nop() on the grain references to ensure that they're instantiated before the promise is delivered.
            await GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(key).Nop();
            return key;
        }

        private static IEnumerable<Task<IPEndPoint>> SampleLocalGrainEndpoint(ILocalPlacementTestGrain grain, int sampleSize)
        {
            for (var i = 0; i < sampleSize; ++i)
                yield return grain.GetEndpoint();
        }

        public async Task<List<IPEndPoint>> SampleLocalGrainEndpoint(Guid key, int sampleSize)
        {
            var grain = GrainFactory.GetGrain<ILocalPlacementTestGrain>(key);
            var p = await Task<IPEndPoint>.WhenAll(SampleLocalGrainEndpoint(grain, sampleSize));
            return p.ToList();
        }

        private static async Task PropigateStatisticsToCluster(IGrainFactory grainFactory)
        {
            // force the latched statistics to propigate throughout the cluster.
            IManagementGrain mgmtGrain =
                grainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            var hosts = await mgmtGrain.GetHosts(true);
            var keys = hosts.Select(kvp => kvp.Key).ToArray();
            await mgmtGrain.ForceRuntimeStatisticsCollection(keys);
        }

        public Task LatchOverloaded()
        {
            Silo.CurrentSilo.Metrics.LatchIsOverload(true);
            return PropigateStatisticsToCluster(GrainFactory);
        }

        public Task UnlatchOverloaded()
        {
            Silo.CurrentSilo.Metrics.UnlatchIsOverloaded();
            return PropigateStatisticsToCluster(GrainFactory);
        }

        public Task LatchCpuUsage(float value)
        {
            Silo.CurrentSilo.Metrics.LatchCpuUsage(value);
            return PropigateStatisticsToCluster(GrainFactory);
        }

        public Task UnlatchCpuUsage()
        {
            Silo.CurrentSilo.Metrics.UnlatchCpuUsage();
            return PropigateStatisticsToCluster(GrainFactory);
        }

        public Task<SiloAddress> GetLocation()
        {
            SiloAddress siloAddress = Data.Address.Silo;
            return Task.FromResult(siloAddress);
        }
    }

    [RandomPlacement]
    internal class RandomPlacementTestGrain : 
        PlacementTestGrainBase, IRandomPlacementTestGrain
    {}

    [PreferLocalPlacement]
    internal class PreferLocalPlacementTestGrain :
       PlacementTestGrainBase, IPreferLocalPlacementTestGrain
    { }

    [StatelessWorker]
    internal class LocalPlacementTestGrain : 
        PlacementTestGrainBase, ILocalPlacementTestGrain
    {}

    [ActivationCountBasedPlacement]
    internal class ActivationCountBasedPlacementTestGrain : 
        PlacementTestGrainBase, IActivationCountBasedPlacementTestGrain
    {}


    //----------------------------------------------------------//
    // Grains for LocalContent grain case, when grain is activated on every silo by bootstrap provider.

    [PreferLocalPlacement]
    public class LocalContentGrain : Grain, ILocalContentGrain
    {
        private Logger logger;
        private object cachedContent;
        internal static ILocalContentGrain InstanceIdForThisSilo;
        
        public override Task OnActivateAsync()
        {
            this.logger = GetLogger();
            logger.Info("OnActivateAsync");
            DelayDeactivation(TimeSpan.MaxValue);   // make sure this activation is not collected.
            cachedContent = RuntimeIdentity;        // store your silo identity as a local cached content in this grain.
            InstanceIdForThisSilo = this.AsReference<ILocalContentGrain>();
            return Task.FromResult(0);
        }

        public Task Init()
        {
            logger.Info("Init LocalContentGrain on silo " + RuntimeIdentity);
            return Task.FromResult(0);
        }

        public Task<object> GetContent()
        {
            return Task.FromResult(cachedContent);
        }
    }

    public class TestContentGrain : Grain, ITestContentGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            this.logger = GetLogger();
            logger.Info("OnActivateAsync");
            return base.OnActivateAsync();
        }

        public Task<string> GetRuntimeInstanceId()
        {
            logger.Info("GetRuntimeInstanceId");
            return Task.FromResult(RuntimeIdentity);
        }

        public async Task<object> FetchContentFromLocalGrain()
        {
            logger.Info("FetchContentFromLocalGrain");
            var localContentGrain = LocalContentGrain.InstanceIdForThisSilo;
            if (localContentGrain == null)
            {
                throw new Exception("LocalContentGrain was not correctly initialized during silo startup!");
            }
            object content = await localContentGrain.GetContent();
            logger.Info("Received content = {0}", content);
            return content;
        }
    }

}
