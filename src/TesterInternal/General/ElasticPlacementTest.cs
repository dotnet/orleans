using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.Elasticity
{
    [TestClass]
    public class ElasticPlacementTests : UnitTestSiloHost
    {
        private readonly List<IActivationCountBasedPlacementTestGrain> grains = new List<IActivationCountBasedPlacementTestGrain>();
        private const int leavy = 300;
        private const int perSilo = 1000;

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = true,
            DataConnectionString = StorageTestConstants.DataConnectionString,
            LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain,
            SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml")
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForTesting.xml"),
            AdjustConfig = config =>
            {
                config.GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;
                config.DataConnectionString = StorageTestConstants.DataConnectionString;
            },
        };

        public ElasticPlacementTests()
            : base(siloOptions, clientOptions)
        { }


        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            grains.Clear();
            RestartAllAdditionalSilos();
            RestartDefaultSilos();
        }

        /// <summary>
        /// Test placement behaviour for newly added silos. The grain placement strategy should favor them
        /// until they reach a similar load as the other silos.
        /// </summary>
        [TestMethod, TestCategory("Functional"), TestCategory("Elasticity"), TestCategory("Placement")]
        public async Task ElasticityTest_CatchingUp()
        {

            logger.Info("\n\n\n----- Phase 1 -----\n\n");
            AddTestGrains(perSilo).Wait();
            AddTestGrains(perSilo).Wait();

            logger.Info("Primary.Silo.Metrics.ActivationCount = {0}", Primary.Silo.Metrics.ActivationCount);
            logger.Info("Secondary.Silo.Metrics.ActivationCount = {0}", Secondary.Silo.Metrics.ActivationCount);
            logger.Info("-----------------------------------------------------------------");
            AssertIsInRange(Primary.Silo.Metrics.ActivationCount, perSilo, leavy);
            AssertIsInRange(Secondary.Silo.Metrics.ActivationCount, perSilo, leavy);
            
            SiloHandle silo3 = StartAdditionalSilo();
            await WaitForLivenessToStabilizeAsync();

            logger.Info("\n\n\n----- Phase 2 -----\n\n");
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

            logger.Info("-----------------------------------------------------------------");
            logger.Info("Primary.Silo.Metrics.ActivationCount = {0}", Primary.Silo.Metrics.ActivationCount);
            logger.Info("Secondary.Silo.Metrics.ActivationCount = {0}", Secondary.Silo.Metrics.ActivationCount);
            logger.Info("silo3.Silo.Metrics.ActivationCount = {0}", silo3.Silo.Metrics.ActivationCount);
            logger.Info("-----------------------------------------------------------------");
            AssertIsInRange(Primary.Silo.Metrics.ActivationCount, (perSilo * 6) / 3, leavy);
            AssertIsInRange(Secondary.Silo.Metrics.ActivationCount, (perSilo * 6) / 3, leavy);
            AssertIsInRange(silo3.Silo.Metrics.ActivationCount, (perSilo * 6) / 3, leavy);

            logger.Info("\n\n\n----- Phase 3 -----\n\n");
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

            logger.Info("-----------------------------------------------------------------");
            logger.Info("Primary.Silo.Metrics.ActivationCount = {0}", Primary.Silo.Metrics.ActivationCount);
            logger.Info("Secondary.Silo.Metrics.ActivationCount = {0}", Secondary.Silo.Metrics.ActivationCount);
            logger.Info("silo3.Silo.Metrics.ActivationCount = {0}", silo3.Silo.Metrics.ActivationCount);
            logger.Info("-----------------------------------------------------------------");
            AssertIsInRange(Primary.Silo.Metrics.ActivationCount, (9 * perSilo) / 3, leavy);
            AssertIsInRange(Secondary.Silo.Metrics.ActivationCount, (9 * perSilo) / 3, leavy);
            AssertIsInRange(silo3.Silo.Metrics.ActivationCount, (9 * perSilo) / 3, leavy);

            logger.Info("-----------------------------------------------------------------");
            logger.Info("Test finished OK. Expected per silo = {0}", (9 * perSilo) / 3);
        }

        /// <summary>
        /// This evaluates the how the placement strategy behaves once silos are stopped: The strategy should
        /// balance the activations from the stopped silo evenly among the remaining silos.
        /// </summary>
        [TestMethod, TestCategory("Functional"), TestCategory("Elasticity"), TestCategory("Placement")]
        public async Task ElasticityTest_StoppingSilos()
        {
            List<SiloHandle> runtimes = StartAdditionalSilos(2);
           await WaitForLivenessToStabilizeAsync();
            int stopLeavy = leavy;

            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);
            await AddTestGrains(perSilo);

            logger.Info("-----------------------------------------------------------------");
            logger.Info("Primary.Silo.Metrics.ActivationCount = {0}", Primary.Silo.Metrics.ActivationCount);
            logger.Info("Secondary.Silo.Metrics.ActivationCount = {0}", Secondary.Silo.Metrics.ActivationCount);
            logger.Info("runtimes[1].Silo.Metrics.ActivationCount = {0}", runtimes[1].Silo.Metrics.ActivationCount);
            logger.Info("-----------------------------------------------------------------");
            AssertIsInRange(Primary.Silo.Metrics.ActivationCount, perSilo, stopLeavy);
            AssertIsInRange(Secondary.Silo.Metrics.ActivationCount, perSilo, stopLeavy);
            AssertIsInRange(runtimes[0].Silo.Metrics.ActivationCount, perSilo, stopLeavy);
            AssertIsInRange(runtimes[1].Silo.Metrics.ActivationCount, perSilo, stopLeavy);

            StopSilo(runtimes[0]);
            await WaitForLivenessToStabilizeAsync();
            await InvokeAllGrains();

            logger.Info("-----------------------------------------------------------------");
            logger.Info("Primary.Silo.Metrics.ActivationCount = {0}", Primary.Silo.Metrics.ActivationCount);
            logger.Info("Secondary.Silo.Metrics.ActivationCount = {0}", Secondary.Silo.Metrics.ActivationCount);
            logger.Info("runtimes[1].Silo.Metrics.ActivationCount = {0}", runtimes[1].Silo.Metrics.ActivationCount);
            logger.Info("-----------------------------------------------------------------");
            AssertIsInRange(Primary.Silo.Metrics.ActivationCount, perSilo * 1.33, stopLeavy);
            AssertIsInRange(Secondary.Silo.Metrics.ActivationCount, perSilo * 1.33, stopLeavy);
            AssertIsInRange(runtimes[1].Silo.Metrics.ActivationCount, perSilo * 1.33, stopLeavy);

            logger.Info("-----------------------------------------------------------------");
            logger.Info("Test finished OK. Expected per silo = {0}", (double)perSilo * 1.33);
        }

        /// <summary>
        /// Do not place activation in case all silos are above 110 CPU utilization.
        /// </summary>
        [TestMethod, TestCategory("Functional"), TestCategory("Elasticity"), TestCategory("Placement")]
        [ExpectedException(typeof(AggregateException))]
        public async Task ElasticityTest_AllSilosCPUTooHigh()
        {
            var taintedGrainPrimary = await GetGrainAtSilo(Primary.Silo.SiloAddress);
            var taintedGrainSecondary = await GetGrainAtSilo(Secondary.Silo.SiloAddress);

            await taintedGrainPrimary.LatchCpuUsage(110.0f);
            await taintedGrainSecondary.LatchCpuUsage(110.0f);
            
            AddTestGrains(1).Wait();
        }

        /// <summary>
        /// Do not place activation in case all silos are above 110 CPU utilization or have overloaded flag set.
        /// </summary>
        [TestMethod, TestCategory("Functional"), TestCategory("Elasticity"), TestCategory("Placement")]
        [ExpectedException(typeof(AggregateException))]
        public async Task ElasticityTest_AllSilosOverloaded()
        {
            var taintedGrainPrimary = await GetGrainAtSilo(Primary.Silo.SiloAddress);
            var taintedGrainSecondary = await GetGrainAtSilo(Secondary.Silo.SiloAddress);

            await taintedGrainPrimary.LatchCpuUsage(110.0f);
            await taintedGrainSecondary.LatchOverloaded();

            await AddTestGrains(1);
        }


        [TestMethod, TestCategory("Functional"), TestCategory("Elasticity"), TestCategory("Placement")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("Elasticity"), TestCategory("Placement")]
        public async Task LoadAwareGrainShouldNotAttemptToCreateActivationsOnBusySilos()
        {
            // a CPU usage of 110% will disqualify a silo from getting new grains.
            const float undesirability = (float) 110.0;
            await ElasticityGrainPlacementTest(
                g =>
                    g.LatchCpuUsage(undesirability),
                g =>
                    g.UnlatchCpuUsage(),
                "LoadAwareGrainShouldNotAttemptToCreateActivationsOnBusySilos",
                "A grain instantiated with the load-aware placement strategy should not attempt to create activations on a busy silo.");
        }


        private async Task<IPlacementTestGrain> GetGrainAtSilo(SiloAddress silo)
        {
            while (true)
            {
                IPlacementTestGrain grain = GrainClient.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid());
                SiloAddress address = await grain.GetLocation();
                if (address.Equals(silo))
                    return grain;
            }
        }

        private static void AssertIsInRange(int actual, double expected, int leavy)
        {
            Assert.IsTrue(expected - leavy <= actual && actual <= expected + leavy,
                String.Format("Expecting a value in the range between {0} and {1}, but instead got {2} outside the range.",
                    expected - leavy, expected + leavy, actual));
        }


        private async Task ElasticityGrainPlacementTest(
            Func<IPlacementTestGrain, Task> taint,
            Func<IPlacementTestGrain, Task> restore,
            string name,
            string assertMsg)
        {
            await WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test {0} ******************************", name);
            var taintedSilo = StartAdditionalSilo().Silo;
            TestSilosStarted(3);

            const long sampleSize = 10;

            var taintedGrain = await GetGrainAtSilo(taintedSilo.SiloAddress);

            var testGrains =
                Enumerable.Range(0, (int)sampleSize).
                Select(
                    n =>
                        GrainClient.GrainFactory.GetGrain<IActivationCountBasedPlacementTestGrain>(Guid.NewGuid()));

            // make the grain's silo undesirable for new grains.
            taint(taintedGrain).Wait();
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
                // i don't know if this necessary but to be safe, i'll restore the silo's desirability.
                logger.Info("********************** Finalizing the test {0} ******************************", name);
                restore(taintedGrain).Wait();
            }

            var unexpected = taintedSilo.SiloAddress.Endpoint;
            Assert.IsTrue(
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
                IActivationCountBasedPlacementTestGrain grain = GrainClient.GrainFactory.GetGrain<IActivationCountBasedPlacementTestGrain>(Guid.NewGuid());
                grains.Add(grain);
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
    }
}
