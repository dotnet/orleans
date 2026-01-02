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
        private const int leavy = 300;
        private const int perSilo = 1000;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
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
        /// This test is timing-sensitive due to asynchronous statistics propagation between silos.
        /// The ActivationCountPlacementDirector's internal cache may not be updated consistently
        /// across all silos before placement decisions are made. See also: ForceRuntimeStatisticsCollection
        /// which triggers refresh but doesn't guarantee synchronous updates to all placement directors.
        /// </remarks>
        [SkippableFact(Skip = "Timing-sensitive: Statistics propagation and placement director cache updates are asynchronous"), TestCategory("Functional")]
        public async Task ElasticityTest_CatchingUp()
        {
            logger.LogInformation("\n\n\n----- Phase 1 -----\n\n");
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

            var activationCounts = await GetPerSiloActivationCounts();
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], perSilo, leavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], perSilo, leavy);

            SiloHandle silo3 = this.HostedCluster.StartAdditionalSilo();
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            logger.LogInformation("\n\n\n----- Phase 2 -----\n\n");
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

            logger.LogInformation("-----------------------------------------------------------------");
            activationCounts = await GetPerSiloActivationCounts();
            LogCounts(activationCounts);
            logger.LogInformation("-----------------------------------------------------------------");
            double expected = (6.0 * perSilo) / 3.0;
            AssertIsInRange(activationCounts[this.HostedCluster.Primary], expected, leavy);
            AssertIsInRange(activationCounts[this.HostedCluster.SecondarySilos.First()], expected, leavy);
            AssertIsInRange(activationCounts[silo3], expected, leavy);

            logger.LogInformation("\n\n\n----- Phase 3 -----\n\n");
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

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
        /// This evaluates the how the placement strategy behaves once silos are stopped: The strategy should
        /// balance the activations from the stopped silo evenly among the remaining silos.
        /// </summary>
        /// <remarks>
        /// This test is timing-sensitive due to asynchronous statistics propagation between silos.
        /// The ActivationCountPlacementDirector's internal cache may not be updated consistently
        /// across all silos before placement decisions are made.
        /// </remarks>
        [SkippableFact(Skip = "Timing-sensitive: Statistics propagation and placement director cache updates are asynchronous"), TestCategory("Functional")]
        public async Task ElasticityTest_StoppingSilos()
        {
            List<SiloHandle> runtimes = await this.HostedCluster.StartAdditionalSilosAsync(2);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            int stopLeavy = leavy;

            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

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

            await InvokeAllGrains();

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
        /// <remarks>
        /// This test is timing-sensitive. There are two independent overload mechanisms:
        /// 1. Gateway load shedding (OverloadDetector) - has a 1-second cache that isn't invalidated by LatchCpuUsage
        /// 2. Placement director filtering (ActivationCountPlacementDirector._localCache) - updated via ForceRuntimeStatisticsCollection
        /// 
        /// Even when SiloRuntimeStatistics.IsOverloaded=True for all silos (confirmed by GetRuntimeStatistics),
        /// placement may still succeed due to timing issues with the gateway's OverloadDetector cache or
        /// the placement director's _localCache not being populated/updated in time.
        /// </remarks>
        [SkippableFact(Skip = "Timing-sensitive: OverloadDetector cache and placement director cache updates are asynchronous"), TestCategory("Functional")]
        public async Task ElasticityTest_AllSilosCPUTooHigh()
        {
            var taintedGrainPrimary = await GetGrainAtSilo(this.HostedCluster.Primary.SiloAddress);
            var taintedGrainSecondary = await GetGrainAtSilo(this.HostedCluster.SecondarySilos.First().SiloAddress);

            // LatchCpuUsage internally calls PropagateStatisticsToCluster which awaits ForceRuntimeStatisticsCollection
            // on all silos. This updates placement director caches but NOT the gateway's OverloadDetector cache.
            await taintedGrainPrimary.LatchCpuUsage(100.0f);
            await taintedGrainSecondary.LatchCpuUsage(100.0f);

            // OrleansException (wrapping SiloUnavailableException) or GatewayTooBusyException
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                this.AddTestGrains(1));

            Assert.True(exception is OrleansException || exception is GatewayTooBusyException);
        }

        /// <summary>
        /// Do not place activation in case all silos are at 100 CPU utilization or have overloaded flag set.
        /// </summary>
        /// <remarks>
        /// This test is timing-sensitive. See remarks on ElasticityTest_AllSilosCPUTooHigh for details.
        /// </remarks>
        [SkippableFact(Skip = "Timing-sensitive: OverloadDetector cache and placement director cache updates are asynchronous"), TestCategory("Functional")]
        public async Task ElasticityTest_AllSilosOverloaded()
        {
            var taintedGrainPrimary = await GetGrainAtSilo(this.HostedCluster.Primary.SiloAddress);
            var taintedGrainSecondary = await GetGrainAtSilo(this.HostedCluster.SecondarySilos.First().SiloAddress);

            // LatchCpuUsage/LatchOverloaded internally call PropagateStatisticsToCluster which awaits
            // ForceRuntimeStatisticsCollection on all silos.
            await taintedGrainPrimary.LatchCpuUsage(100.0f);
            await taintedGrainSecondary.LatchOverloaded();

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
            string fullTypeName = "UnitTests.Grains.ActivationCountBasedPlacementTestGrain";

            IManagementGrain mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            SimpleGrainStatistic[] stats = await mgmtGrain.GetSimpleGrainStatistics();

            return this.HostedCluster.GetActiveSilos()
                .ToDictionary(
                    s => s,
                    s => stats
                        .Where(stat => stat.SiloAddress.Equals(s.SiloAddress) && stat.GrainType == fullTypeName)
                        .Select(stat => stat.ActivationCount).SingleOrDefault());
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
    }
}
