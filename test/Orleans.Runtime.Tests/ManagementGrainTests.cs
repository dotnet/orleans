using System.Text;
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
    /// <summary>
    /// Tests for the Orleans Management Grain, which provides runtime statistics and cluster management.
    /// 
    /// The Management Grain (IManagementGrain) is a system grain that provides:
    /// - Cluster topology information (active silos, their status)
    /// - Grain activation statistics (counts per type, per silo)
    /// - Runtime metrics and diagnostics
    /// 
    /// These capabilities are essential for:
    /// - Monitoring cluster health
    /// - Debugging activation distribution
    /// - Building management dashboards
    /// - Implementing custom placement strategies based on current load
    /// </summary>
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
                // between silos is obtained using external processes. This ensures each silo maintains independent
                // statistics rather than sharing them through static fields in the same process.
                builder.CreateSiloAsync = StandaloneSiloHandle.CreateForAssembly(this.GetType().Assembly);
                builder.Properties["GrainAssembly"] = $"{typeof(SimpleGrain).Assembly}";
            }
        }

        /// <summary>
        /// Tests retrieval of cluster topology information.
        /// GetHosts returns all active silos in the cluster with their current status.
        /// This is fundamental for understanding cluster composition and health.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public async Task GetHosts()
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                await HostedCluster.StartAdditionalSiloAsync();
                await HostedCluster.WaitForLivenessToStabilizeAsync();
            }

            var numberOfActiveSilos = 1 + HostedCluster.SecondarySilos.Count; // Primary + secondaries
            Dictionary<SiloAddress, SiloStatus> siloStatuses = await mgmtGrain.GetHosts(true);
            Assert.NotNull(siloStatuses);
            Assert.Equal(numberOfActiveSilos, siloStatuses.Count);
        }

        /// <summary>
        /// Tests retrieval of detailed host information including additional metadata.
        /// GetDetailedHosts provides more comprehensive information about each silo,
        /// useful for detailed diagnostics and monitoring dashboards.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public async Task GetDetailedHosts()
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                await HostedCluster.StartAdditionalSiloAsync();
                await HostedCluster.WaitForLivenessToStabilizeAsync();
            }

            var numberOfActiveSilos = 1 + HostedCluster.SecondarySilos.Count; // Primary + secondaries
            var siloStatuses = await mgmtGrain.GetDetailedHosts(true);
            Assert.NotNull(siloStatuses);
            Assert.Equal(numberOfActiveSilos, siloStatuses.Length);
        }


        /// <summary>
        /// Tests basic grain statistics retrieval.
        /// Verifies that:
        /// - Statistics are returned for active grain types
        /// - Grain type names are properly formatted (without 'Activation' suffix)
        /// This data is crucial for understanding what grains are active in the cluster.
        /// </summary>
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

        /// <summary>
        /// Tests that activation counts are correctly tracked for SimpleGrain.
        /// Verifies that:
        /// - Creating a new grain activation increments the count
        /// - Statistics accurately reflect the number of active grains
        /// This is essential for load monitoring and placement decisions.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public async Task GetSimpleGrainStatistics_ActivationCounts()
        {
            await RunGetStatisticsTest<ISimpleGrain, SimpleGrain>(g => g.GetA());
        }

        [Fact, TestCategory("BVT"), TestCategory("Management")]
        public async Task GetTestGrainStatistics_ActivationCounts()
        {
            await RunGetStatisticsTest<ITestGrain, TestGrain>(g => g.GetKey());
        }

        /// <summary>
        /// Generic test helper that verifies activation counting for any grain type.
        /// Process:
        /// 1. Get initial statistics and count existing activations
        /// 2. Create and activate a new grain by calling a method
        /// 3. Verify the activation count increased by exactly 1
        /// This ensures the management grain correctly tracks grain lifecycle events.
        /// </summary>
        private async Task RunGetStatisticsTest<TGrainInterface, TGrain>(Func<TGrainInterface, Task> callGrainMethodAction)
            where TGrainInterface : IGrainWithIntegerKey
            where TGrain : TGrainInterface
        {
            SimpleGrainStatistic[] stats = this.GetSimpleGrainStatisticsRunner("Before Create");
            Assert.True(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = RuntimeTypeNameFormatter.Format(typeof(TGrain));
            int initialStatisticsCount = stats.Count(s => s.GrainType == grainType);
            int initialActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            var grain1 = this.fixture.Client.GetGrain<TGrainInterface>(Random.Shared.Next());
            await callGrainMethodAction(grain1); // Call grain method to ensure activation
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
