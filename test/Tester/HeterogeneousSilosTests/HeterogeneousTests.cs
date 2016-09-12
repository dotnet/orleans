using System;
using System.Linq;
using System.Threading;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.HeterogeneousSilosTests
{
    public class HeterogeneousTests : OrleansTestingBase, IDisposable
    {
        private TestCluster cluster;

        private void SetupAndDeployCluster(params Type[] blackListedTypes)
        {
            var typesName = blackListedTypes.Select(t => t.FullName).ToList();
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.Overrides[Silo.PrimarySiloName].ExcludedGrainTypes = typesName;
            cluster = new TestCluster(options);
            cluster.Deploy();
        }

        public void Dispose()
        {
            cluster?.StopAllSilos();
        }

        [Fact]
        public void GrainExcludedTest()
        {
            SetupAndDeployCluster(typeof(TestGrain));

            // Should fail
            var exception = Assert.Throws<ArgumentException>(() => GrainFactory.GetGrain<ITestGrain>(0));
            Assert.Contains("Cannot find an implementation class for grain interface", exception.Message);

            // Should not fail
            GrainFactory.GetGrain<ISimpleGrainWithAsyncMethods>(0);
        }
    }
}
