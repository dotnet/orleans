using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    [TestCategory("Elasticity"), TestCategory("Placement")]
    public class ElasticPlacementTests : TestClusterPerTest
    {
        private readonly List<IActivationCountBasedPlacementTestGrain> grains = new List<IActivationCountBasedPlacementTestGrain>();
        private const int leavy = 350; // Tolerance for placement variance (17.5% of perSilo)
        private const int perSilo = 1000;
        private PlacementDiagnosticObserver _placementObserver;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            _placementObserver = PlacementDiagnosticObserver.Create(logger);
        }

        public override async Task DisposeAsync()
        {
            _placementObserver?.Dispose();
            await base.DisposeAsync();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorageAsDefault()
                    .Configure<LoadSheddingOptions>(options => options.LoadSheddingEnabled = true);
            }
        }

        /// <summary>
        /// Test placement behaviour for newly added silos. The grain placement strategy should favor them
        /// until they reach a similar load as the other silos.
        /// </summary>
        /// <remarks>
        /// This test uses deterministic statistics synchronization via ForceRuntimeStatisticsCollection
        /// to ensure all placement directors have accurate activation counts before grain placement.
        /// After each cluster membership change, we force statistics collection on all silos.
        /// </remarks>
        [Fact, TestCategory("Functional")]
        public async Task ElasticityTest_CatchingUp()
        {
            logger.LogInformation("\n\n\n----- Phase 1 -----\n\n");
            
            // Ensure initial statistics are propagated before creating grains
            await ForceStatisticsRefreshOnAllSilos();
            
            // Create grains in batches with stats refresh between each batch to ensure
            // placement directors have accurate activation counts during placement.
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);

            // Force statistics refresh so placement directors have accurate counts
            await ForceStatisticsRefreshOnAllSilos();

            var activationCounts = await GetPerSiloActivationCounts();
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], perSilo, leavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], perSilo, leavy);

            SiloHandle silo3 = this.HostedCluster.StartAdditionalSilo();
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            
            // Critical: After a new silo joins, force statistics refresh so all silos know about each other's activation counts.
            // Without this, the new silo won't be considered by placement directors on other silos.
            await ForceStatisticsRefreshOnAllSilos();

            logger.LogInformation("\n\n\n----- Phase 2 -----\n\n");
            // Create grains in batches with stats refresh between each batch
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);

            // Force statistics refresh before assertions
            await ForceStatisticsRefreshOnAllSilos();

            logger.LogInformation("-----------------------------------------------------------------");
            activationCounts = await GetPerSiloActivationCounts();
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            double expected = (6.0 * perSilo) / 3.0;
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], expected, leavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], expected, leavy);
            AssertIsInRange(activationCounts[silo3], expected, leavy);

            logger.LogInformation("\n\n\n----- Phase 3 -----\n\n");
            // Create grains in batches with stats refresh between each batch
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);

            // Force statistics refresh before final assertions
            await ForceStatisticsRefreshOnAllSilos();

            logger.LogInformation("-----------------------------------------------------------------");
            activationCounts = await GetPerSiloActivationCounts();
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            expected = (9.0 * perSilo) / 3.0;
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], expected, leavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], expected, leavy);
            AssertIsInRange(activationCounts[silo3], expected, leavy);

            logger.LogInformation("-----------------------------------------------------------------");
            logger.LogInformation("Test finished OK. Expected per silo = {Expected}", expected);
        }

        /// <summary>
        /// This evaluates how the placement strategy behaves once silos are stopped: The strategy should
        /// balance the activations from the stopped silo evenly among the remaining silos.
        /// </summary>
        /// <remarks>
        /// This test uses deterministic statistics synchronization via ForceRuntimeStatisticsCollection
        /// to ensure all placement directors have accurate activation counts before grain placement.
        /// </remarks>
        [Fact, TestCategory("Functional")]
        public async Task ElasticityTest_StoppingSilos()
        {
            List<SiloHandle> runtimes = await this.HostedCluster.StartAdditionalSilosAsync(2);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            
            // Ensure statistics are propagated after new silos join
            await ForceStatisticsRefreshOnAllSilos();

            int stopLeavy = leavy;

            // Create grains in batches with stats refresh between each batch to ensure
            // placement directors have accurate activation counts during placement.
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);
            await ForceStatisticsRefreshOnAllSilos();
            await AddTestGrains(perSilo);
            
            // Force statistics refresh before assertions
            await ForceStatisticsRefreshOnAllSilos();

            var activationCounts = await GetPerSiloActivationCounts();
            logger.LogInformation("-----------------------------------------------------------------");
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], perSilo, stopLeavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], perSilo, stopLeavy);
            AssertIsInRange(activationCounts[runtimes[0]], perSilo, stopLeavy);
            AssertIsInRange(activationCounts[runtimes[1]], perSilo, stopLeavy);

            await this.HostedCluster.StopSiloAsync(runtimes[0]);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            
            // Force statistics refresh after silo stops so placement directors know about the removed silo
            await ForceStatisticsRefreshOnAllSilos();

            await InvokeAllGrains();
            
            // Force statistics refresh before final assertions
            await ForceStatisticsRefreshOnAllSilos();

            activationCounts = await GetPerSiloActivationCounts();
            logger.LogInformation("-----------------------------------------------------------------");
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            double expected = perSilo * 1.33;
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], expected, stopLeavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], expected, stopLeavy);
            AssertIsInRange(activationCounts[runtimes[1]], expected, stopLeavy);

            logger.LogInformation("-----------------------------------------------------------------");
            logger.LogInformation("Test finished OK. Expected per silo = {Expected}", expected);
        }

        /// <summary>
        /// Do not place activation in case all silos are at 100 CPU utilization.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task ElasticityTest_AllSilosCPUTooHigh()
        {
            var taintedGrainPrimary = await GetGrainAtSilo(this.HostedCluster.Primary.SiloAddress);
            var taintedGrainSecondary = await GetGrainAtSilo(this.HostedCluster.SecondarySilos.First().SiloAddress);

            // Latch CPU on all silos and refresh overload detectors in parallel.
            // This ensures all silos are tainted before any gateway starts rejecting requests.
            await Task.WhenAll(
                taintedGrainPrimary.LatchCpuUsageOnly(100.0f),
                taintedGrainSecondary.LatchCpuUsageOnly(100.0f));

            // Now refresh OverloadDetector caches and propagate statistics in parallel.
            // After this, both silos will reject requests due to overload.
            await Task.WhenAll(
                taintedGrainPrimary.RefreshOverloadDetectorAndPropagateStatistics(),
                taintedGrainSecondary.RefreshOverloadDetectorAndPropagateStatistics());

            // OrleansException (wrapping SiloUnavailableException) or GatewayTooBusyException
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                this.AddTestGrains(1));

            Assert.True(exception is OrleansException || exception is GatewayTooBusyException);
        }

        /// <summary>
        /// Do not place activation in case all silos are at 100 CPU utilization or have overloaded flag set.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task ElasticityTest_AllSilosOverloaded()
        {
            var taintedGrainPrimary = await GetGrainAtSilo(this.HostedCluster.Primary.SiloAddress);
            var taintedGrainSecondary = await GetGrainAtSilo(this.HostedCluster.SecondarySilos.First().SiloAddress);

            // Atomically latch and propagate on all silos in parallel.
            // Using the atomic methods avoids the race condition where OverloadDetector
            // auto-refreshes between latching and explicit refresh, causing the silo
            // to reject subsequent RPC calls.
            await Task.WhenAll(
                taintedGrainPrimary.LatchCpuUsageAndPropagate(100.0f),
                taintedGrainSecondary.LatchOverloadedAndPropagate());

            // OrleansException or GatewayTooBusyException
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                this.AddTestGrains(1));

            Assert.True(exception is OrleansException || exception is GatewayTooBusyException);
        }


        [Fact, TestCategory("Functional")]
        public async Task LoadAwareGrainShouldNotAttemptToCreateActivationsOnOverloadedSilo()
        {
            await ElasticityGrainPlacementTest(
                g =>
                    g.LatchOverloaded(),
                g =>
                    g.UnlatchOverloaded(),
                "LoadAwareGrainShouldNotAttemptToCreateActivationsOnOverloadedSilo",
                "A grain instantiated with the load-aware placement strategy should not attempt to create activations on an overloaded silo.");
        }

        [Fact, TestCategory("Functional")]
        public async Task LoadAwareGrainShouldNotAttemptToCreateActivationsOnBusySilos()
        {
            // a CPU usage of 100% will disqualify a silo from getting new grains.
            await ElasticityGrainPlacementTest(
                g =>
                    g.LatchCpuUsage(100.0f),
                g =>
                    g.UnlatchCpuUsage(),
                "LoadAwareGrainShouldNotAttemptToCreateActivationsOnBusySilos",
                "A grain instantiated with the load-aware placement strategy should not attempt to create activations on a busy silo.");
        }


        private async Task<IPlacementTestGrain> GetGrainAtSilo(SiloAddress silo)
        {
            while (true)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, silo);
                IPlacementTestGrain grain = this.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid());
                SiloAddress address = await grain.GetLocation();
                if (address.Equals(silo))
                    return grain;
            }
        }

        private static void AssertIsInRange(int actual, double expected, int leavy)
        {
            Assert.True(expected - leavy <= actual && actual <= expected + leavy,
                string.Format("Expecting a value in the range between {0} and {1}, but instead got {2} outside the range.",
                    expected - leavy, expected + leavy, actual));
        }


        private async Task ElasticityGrainPlacementTest(
            Func<IPlacementTestGrain, Task> taint,
            Func<IPlacementTestGrain, Task> restore,
            string name,
            string assertMsg)
        {
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            logger.LogInformation("********************** Starting the test {Name} ******************************", name);
            var taintedSilo = this.HostedCluster.StartAdditionalSilo();
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            // Ensure all silos have the new silo in their placement statistics cache
            // before we taint it. Without this, placement directors may not know about
            // the new silo and fail to find candidates.
            await ForceStatisticsRefreshOnAllSilos();

            const long sampleSize = 10;

            var taintedGrain = await GetGrainAtSilo(taintedSilo.SiloAddress);

            var testGrains =
                Enumerable.Range(0, (int)sampleSize).
                Select(
                    n =>
                        this.GrainFactory.GetGrain<IActivationCountBasedPlacementTestGrain>(Guid.NewGuid()));

            // Make the grain's silo undesirable for new grains.
            // taint() calls LatchOverloaded/LatchCpuUsage which internally awaits PropagateStatisticsToCluster,
            // so by the time this returns, all placement directors have been notified.
            await taint(taintedGrain);

            List<IPEndPoint> actual;
            try
            {
                actual =
                    testGrains.Select(
                        g =>
                            g.GetEndpoint().Result).ToList();
            }
            finally
            {
                // Restore the silo's desirability.
                logger.LogInformation("********************** Finalizing the test {Name} ******************************", name);
                await restore(taintedGrain);
            }

            var unexpected = taintedSilo.SiloAddress.Endpoint;
            Assert.True(
                actual.All(
                    i =>
                        !i.Equals(unexpected)),
                assertMsg);
        }

        private Task AddTestGrains(int amount)
        {
            var promises = new List<Task>();
            for (var i = 0; i < amount; i++)
            {
                IActivationCountBasedPlacementTestGrain grain = this.GrainFactory.GetGrain<IActivationCountBasedPlacementTestGrain>(Guid.NewGuid());
                this.grains.Add(grain);
                // Make sure we activate grain:
                promises.Add(grain.Nop());
            }
            return Task.WhenAll(promises);
        }

        private Task InvokeAllGrains()
        {
            var promises = new List<Task>();
            foreach (var grain in grains)
            {
                promises.Add(grain.Nop());
            }
            return Task.WhenAll(promises);
        }

        private async Task<Dictionary<SiloHandle, int>> GetPerSiloActivationCounts()
        {
            // Note: We use GetDetailedGrainStatistics instead of GetSimpleGrainStatistics because
            // GetSimpleGrainStatistics uses a static GrainMetricsListener which is shared across all
            // in-process silos (causing double-counting). GetDetailedGrainStatistics uses the per-silo
            // activation directory which correctly tracks activations per silo.
            string grainTypePrefix = "UnitTests.Grains.ActivationCountBasedPlacementTestGrain";

            IManagementGrain mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            DetailedGrainStatistic[] stats = await mgmtGrain.GetDetailedGrainStatistics();

            // Filter to just our test grain type and group by silo
            var filteredStats = stats
                .Where(stat => stat.GrainType.StartsWith(grainTypePrefix))
                .GroupBy(stat => stat.SiloAddress)
                .ToDictionary(g => g.Key, g => g.Count());

            return this.HostedCluster.GetActiveSilos()
                .ToDictionary(
                    s => s,
                    s => filteredStats.TryGetValue(s.SiloAddress, out var count) ? count : 0);
        }

        private void LogCounts(Dictionary<SiloHandle, int> activationCounts)
        {
            var sb = new StringBuilder();
            foreach (var silo in this.HostedCluster.GetActiveSilos())
            {
                int count;
                activationCounts.TryGetValue(silo, out count);
                sb.AppendLine($"{silo.Name}.ActivationCount = {count}");
            }
            logger.LogInformation("{Message}", sb.ToString());
        }

        /// <summary>
        /// Forces statistics refresh on all active silos and waits for ClusterStatisticsRefreshed events.
        /// This ensures all placement directors have up-to-date activation counts before grain placement.
        /// </summary>
        private async Task ForceStatisticsRefreshOnAllSilos()
        {
            // Clear previous events to track new refresh cycle
            _placementObserver.Clear();

            // Get all active silo addresses
            var siloAddresses = this.HostedCluster.GetActiveSilos()
                .Select(s => s.SiloAddress)
                .ToArray();

            // Trigger statistics collection on all silos
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmtGrain.ForceRuntimeStatisticsCollection(siloAddresses);

            // Wait for all silos to emit ClusterStatisticsRefreshed event
            await _placementObserver.WaitForAllSilosRefreshedAsync(siloAddresses, TimeSpan.FromSeconds(30));
        }
    }
}
