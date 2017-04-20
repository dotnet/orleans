using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable ConvertToConstant.Local

namespace DefaultCluster.Tests.Management
{
    public class ManagementGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly ITestOutputHelper output;
        private IManagementGrain mgmtGrain;
        
        public ManagementGrainTests(DefaultClusterFixture fixture, ITestOutputHelper output)
            : base(fixture)
        {
            this.output = output;
            mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Management")]
        public async Task GetHosts()
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                HostedCluster.StartAdditionalSilo();
                await HostedCluster.WaitForLivenessToStabilizeAsync();
            }

            var numberOfActiveSilos = 1 + HostedCluster.SecondarySilos.Count; // Primary + secondaries
            Dictionary<SiloAddress, SiloStatus> siloStatuses = mgmtGrain.GetHosts(true).Result;
            Assert.NotNull(siloStatuses);
            Assert.Equal(numberOfActiveSilos, siloStatuses.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Management")]
        public async Task GetDetailedHosts()
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                HostedCluster.StartAdditionalSilo();
                await HostedCluster.WaitForLivenessToStabilizeAsync();
            }

            var numberOfActiveSilos = 1 + HostedCluster.SecondarySilos.Count; // Primary + secondaries
            var siloStatuses = mgmtGrain.GetDetailedHosts(true).Result;
            Assert.NotNull(siloStatuses);
            Assert.Equal(numberOfActiveSilos, siloStatuses.Length);
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Management")]
        public void GetSimpleGrainStatistics()
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Initial");
            Assert.True(stats.Length > 0, "Got some grain statistics: " + stats.Length);
            foreach (var s in stats)
            {
                Assert.False(s.GrainType.EndsWith("Activation"), "Grain type name should not end with 'Activation' - " + s.GrainType);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Management")]
        public void GetSimpleGrainStatistics_ActivationCounts()
        {
            RunGetStatisticsTest<ISimpleGrain, SimpleGrain>(g => g.GetA().Wait());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Management")]
        public void GetTestGrainStatistics_ActivationCounts()
        {
            RunGetStatisticsTest<ITestGrain, TestGrain>(g => g.GetKey().Wait());
        }

        private void RunGetStatisticsTest<TGrainInterface, TGrain>(Action<TGrainInterface> callGrainMethodAction)
            where TGrainInterface : IGrainWithIntegerKey
            where TGrain : TGrainInterface
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Before Create");
            Assert.True(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = typeof(TGrain).FullName;
            int initialStatisticsCount = stats.Count(s => s.GrainType == grainType);
            int initialActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            var grain1 = this.GrainFactory.GetGrain<TGrainInterface>(random.Next());
            callGrainMethodAction(grain1); // Call grain method
            stats = GetSimpleGrainStatistics("After Invoke");
            Assert.True(stats.Count(s => s.GrainType == grainType) >= initialStatisticsCount, "Activation counter now exists for grain: " + grainType);
            int expectedActivationsCount = initialActivationsCount + 1;
            int actualActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            Assert.Equal(expectedActivationsCount, actualActivationsCount);
        }

        private SimpleGrainStatistic[] GetSimpleGrainStatistics(string when)
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

// ReSharper restore ConvertToConstant.Local
