using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

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
            var builder = new TestClusterBuilder(1);
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.AssumeHomogenousSilosForTesting = false;
                legacy.ClusterConfiguration.Globals.TypeMapRefreshInterval = refreshInterval;
                legacy.ClusterConfiguration.Globals.DefaultPlacementStrategy = defaultPlacementStrategy;
                legacy.ClusterConfiguration.GetOrCreateNodeConfigurationForSilo(Silo.PrimarySiloName).ExcludedGrainTypes = typesName;
            });
            cluster = builder.Build();
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
            // TODO Check ActivationCountBasedPlacement in tests
            //await MergeGrainResolverTestsImpl("ActivationCountBasedPlacement", typeof(TestGrain));
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
            await cluster.Client.Close();
            cluster.Client.Dispose();
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
            await cluster.Client.Close();
            cluster.Client.Dispose();
            cluster.InitializeClient();

            // Should fail
            exception = Assert.Throws<ArgumentException>(() => this.cluster.GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);
        }
    }
}
