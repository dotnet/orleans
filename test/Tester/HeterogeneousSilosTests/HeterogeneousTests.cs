using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.HeterogeneousSilosTests
{
    [TestCategory("Functional")]
    public class HeterogeneousTests : OrleansTestingBase, IDisposable
    {
        private TestCluster cluster;
        private readonly TimeSpan refreshInterval = TimeSpan.FromMilliseconds(200);

        private void SetupAndDeployCluster(string defaultPlacementStrategy, params Type[] blackListedTypes)
        {
            cluster?.StopAllSilos();
            var typesName = blackListedTypes.Select(t => t.FullName).ToList();
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.Globals.AssumeHomogenousSilosForTesting = false;
            options.ClusterConfiguration.Globals.TypeMapRefreshInterval = refreshInterval;
            options.ClusterConfiguration.Globals.DefaultPlacementStrategy = defaultPlacementStrategy;
            options.ClusterConfiguration.Overrides[Silo.PrimarySiloName].ExcludedGrainTypes = typesName;
            cluster = new TestCluster(options);
            cluster.Deploy();
        }

        public void Dispose()
        {
            cluster?.StopAllSilos();
            cluster = null;
        }

        [Fact]
        public void GrainExcludedTest()
        {
            SetupAndDeployCluster("RandomPlacement", typeof(TestGrain));

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);

            // Should not fail
            this.cluster.GrainFactory.GetGrain<ISimpleGrainWithAsyncMethods>(0);
        }


        [Fact]
        public async Task MergeGrainResolverTests()
        {
            await MergeGrainResolverTestsImpl("RandomPlacement", typeof(TestGrain));
            await MergeGrainResolverTestsImpl("PreferLocalPlacement", typeof(TestGrain));
            await MergeGrainResolverTestsImpl("ActivationCountBasedPlacement", typeof(TestGrain));
        }

        private async Task MergeGrainResolverTestsImpl(string defaultPlacementStrategy, params Type[] blackListedTypes)
        {
            SetupAndDeployCluster(defaultPlacementStrategy, blackListedTypes);

            var delayTimeout = refreshInterval.Add(refreshInterval);

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);

            // Start a new silo with TestGrain
            cluster.StartAdditionalSilo();
            await Task.Delay(delayTimeout);

            // Disconnect/Reconnect the client
            GrainClient.Uninitialize();
            cluster.InitializeClient();

            for (var i = 0; i < 5; i++)
            {
                // Success
                var g = this.cluster.GrainFactory.GetGrain<ITestGrain>(i);
                await g.SetLabel("Hello world");
            }

            // Stop the latest silos
            cluster.StopSecondarySilos();
            await Task.Delay(delayTimeout);

            var grain = this.cluster.GrainFactory.GetGrain<ITestGrain>(0);
            var orleansException = await Assert.ThrowsAsync<OrleansException>(() => grain.SetLabel("Hello world"));
            Assert.Contains("Cannot find an implementation class for grain interface", orleansException.Message);

            // Disconnect/Reconnect the client
            GrainClient.Uninitialize();
            cluster.InitializeClient();

            // Should fail
            exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);
        }
    }
}
