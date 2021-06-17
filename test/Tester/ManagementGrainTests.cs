using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.Management
{
    public class ManagementGrainTests :  OrleansTestingBase, IClassFixture<ManagementGrainTests.Fixture>
    {
        private readonly Fixture fixture;
        private readonly ITestOutputHelper output;
        private readonly IManagementGrain mgmtGrain;
        
        public ManagementGrainTests(Fixture fixture, ITestOutputHelper output)
        {
            this.fixture = fixture;
            this.output = output;
            mgmtGrain = this.fixture.Client.GetGrain<IManagementGrain>(0);
        }

        private TestCluster HostedCluster => this.fixture.HostedCluster;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                // The ActivationCount tests rely on CounterStatistic, which is a shared static value, so isolation
                // between silos is obtained using AppDomains.
                builder.CreateSiloAsync = StandaloneSiloHandle.CreateForAssembly(this.GetType().Assembly);
;
                builder.Properties["GrainAssembly"] = $"{typeof(SimpleGrain).Assembly}";
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public async Task GetHosts()
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                await HostedCluster.StartAdditionalSiloAsync();
                await HostedCluster.WaitForLivenessToStabilizeAsync();
            }

            var numberOfActiveSilos = 1 + HostedCluster.SecondarySilos.Count; // Primary + secondaries
            Dictionary<SiloAddress, SiloStatus> siloStatuses = mgmtGrain.GetHosts(true).Result;
            Assert.NotNull(siloStatuses);
            Assert.Equal(numberOfActiveSilos, siloStatuses.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public async Task GetDetailedHosts()
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                await HostedCluster.StartAdditionalSiloAsync();
                await HostedCluster.WaitForLivenessToStabilizeAsync();
            }

            var numberOfActiveSilos = 1 + HostedCluster.SecondarySilos.Count; // Primary + secondaries
            var siloStatuses = mgmtGrain.GetDetailedHosts(true).Result;
            Assert.NotNull(siloStatuses);
            Assert.Equal(numberOfActiveSilos, siloStatuses.Length);
        }


        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public void GetSimpleGrainStatistics()
        {
            SimpleGrainStatistic[] stats = this.GetSimpleGrainStatisticsRunner("Initial");
            Assert.True(stats.Length > 0, "Got some grain statistics: " + stats.Length);
            foreach (var s in stats)
            {
                Assert.False(s.GrainType.EndsWith("Activation"), "Grain type name should not end with 'Activation' - " + s.GrainType);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public void GetSimpleGrainStatistics_ActivationCounts()
        {
            RunGetStatisticsTest<ISimpleGrain, SimpleGrain>(g => g.GetA().Wait());
        }

        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public void GetTestGrainStatistics_ActivationCounts()
        {
            RunGetStatisticsTest<ITestGrain, TestGrain>(g => g.GetKey().Wait());
        }

        private void RunGetStatisticsTest<TGrainInterface, TGrain>(Action<TGrainInterface> callGrainMethodAction)
            where TGrainInterface : IGrainWithIntegerKey
            where TGrain : TGrainInterface
        {
            SimpleGrainStatistic[] stats = this.GetSimpleGrainStatisticsRunner("Before Create");
            Assert.True(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = RuntimeTypeNameFormatter.Format(typeof(TGrain));
            int initialStatisticsCount = stats.Count(s => s.GrainType == grainType);
            int initialActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            var grain1 = this.fixture.Client.GetGrain<TGrainInterface>(random.Next());
            callGrainMethodAction(grain1); // Call grain method
            stats = this.GetSimpleGrainStatisticsRunner("After Invoke");
            Assert.True(stats.Count(s => s.GrainType == grainType) >= initialStatisticsCount, "Activation counter now exists for grain: " + grainType);
            int expectedActivationsCount = initialActivationsCount + 1;
            int actualActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            Assert.Equal(expectedActivationsCount, actualActivationsCount);
        }

        private SimpleGrainStatistic[] GetSimpleGrainStatisticsRunner(string when)
        {
            SimpleGrainStatistic[] stats = mgmtGrain.GetSimpleGrainStatistics(null).Result;
            StringBuilder sb = new StringBuilder();
            foreach (var s in stats) sb.AppendLine().Append(s);
            sb.AppendLine();
            output.WriteLine("Grain statistics returned by Orleans Management Grain - " + when + " : " + sb);
            return stats;
        }
    }
}
