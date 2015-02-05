using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;

using Orleans.Runtime;


using UnitTestGrainInterfaces;

namespace UnitTests
{
    [TestClass]
    public class GrainPlacementTests : UnitTestBase
    {
        public GrainPlacementTests()
            : base(new Options
                    {
                        StartFreshOrleans = true,
                    }, new ClientOptions
                    {
                        ProxiedGateway = true,
                        Gateways = new List<IPEndPoint>(new IPEndPoint[] { new IPEndPoint(IPAddress.Loopback, 30000), new IPEndPoint(IPAddress.Loopback, 30001) }),
                        PreferedGatewayIndex = -1
                    })
        {
        }

        //[TestInitialize]
        //public void TestInitialize()
        //{
        //    ResetAllAdditionalRuntimes();
        //    ResetDefaultRuntimes();
        //}

        [TestCleanup()]
        public void TestCleanup()
        {
            ResetAllAdditionalRuntimes();
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Placement"), TestCategory("Nightly")]
        public void DefaultPlacementShouldBeRandom()
        {
            WaitForLivenessToStabilize();
            logger.Info("********************** Starting the test DefaultPlacementShouldBeRandom ******************************");
            TestSilosStarted(2);

            Assert.AreEqual(
                RandomPlacement.Singleton,
                PlacementStrategy.GetDefault(),
                "The default placement strategy is expected to be random.");
        }

        [TestMethod, TestCategory("Placement"), TestCategory("Nightly")]
        public void RandomlyPlacedGrainShouldPlaceActivationsRandomly()
        {
            WaitForLivenessToStabilize();
            logger.Info("********************** Starting the test RandomlyPlacedGrainShouldPlaceActivationsRandomly ******************************");
            TestSilosStarted(2);

            logger.Info("********************** TestSilosStarted passed OK. ******************************");

            var placement = RandomPlacement.Singleton;
            var grains =
                Enumerable.Range(0, 20).
                Select(
                    n =>
                        RandomPlacementTestGrainFactory.GetGrain((long)n));
            var places = grains.Select(g => g.GetRuntimeInstanceId().Result);
            var placesAsArray = places as string[] ?? places.ToArray();
            // consider: it seems like we should check that we get close to a 50/50 split for placement.
            var groups = placesAsArray.GroupBy(s => s);
            Assert.IsTrue(groups.Count() > 1,
                "Grains should be on different silos, but they are on " + Utils.EnumerableToString(placesAsArray.ToArray())); // will randomly fail one in a million times if RNG is good :-)
        }

        //[TestMethod, TestCategory("Placement"), TestCategory("Nightly")]
        //public void PreferLocalPlacedGrainShouldPlaceActivationsLocally_OneHop()
        //{
        //    WaitForLivenessToStabilize();
        //    logger.Info("********************** Starting the test PreferLocalPlacedGrainShouldPlaceActivationsLocally ******************************");
        //    TestSilosStarted(2);

        //    int numGrains = 20;
        //    var preferLocalGrain =
        //        Enumerable.Range(0, numGrains).
        //            Select(
        //                n =>
        //                    PreferLocalPlacementTestGrainFactory.GetGrain((long)n)).ToList();
        //    var preferLocalGrainPlaces = preferLocalGrain.Select(g => g.GetRuntimeInstanceId().Result).ToList();

        //    // check that every "prefer local grain" was placed on the same silo with its requesting random grain
        //    foreach (int key in Enumerable.Range(0, numGrains))
        //    {
        //        string preferLocal = preferLocalGrainPlaces.ElementAt(key);
        //        logger.Info(preferLocal);
        //    }
        //}

        [TestMethod, TestCategory("Placement"), TestCategory("Nightly")]
        public void PreferLocalPlacedGrainShouldPlaceActivationsLocally_TwoHops()
        {
            WaitForLivenessToStabilize();
            logger.Info("********************** Starting the test PreferLocalPlacedGrainShouldPlaceActivationsLocally ******************************");
            TestSilosStarted(2);

            int numGrains = 20;
            var randomGrains =
                Enumerable.Range(0, numGrains).
                    Select(
                        n =>
                            RandomPlacementTestGrainFactory.GetGrain((long)n)).ToList();
            var randomGrainPlaces = randomGrains.Select(g => g.GetRuntimeInstanceId().Result).ToList();

            var preferLocalGrainKeys =
                randomGrains.
                    Select(
                        (IRandomPlacementTestGrain g) =>
                            g.StartPreferLocalGrain(g.GetPrimaryKeyLong()).Result).ToList();
            var preferLocalGrainPlaces = preferLocalGrainKeys.Select(key => PreferLocalPlacementTestGrainFactory.GetGrain(key).GetRuntimeInstanceId().Result).ToList();

            // check that every "prefer local grain" was placed on the same silo with its requesting random grain
            foreach(int key in Enumerable.Range(0, numGrains))
            {
                string random = randomGrainPlaces.ElementAt(key);
                string preferLocal = preferLocalGrainPlaces.ElementAt(key);
                Assert.AreEqual(random, preferLocal,
                    "Grains should be on the same silos, but they are on " + random + " and " + preferLocal);
            }
        }

        private IEnumerable<IPEndPoint> SampleEndpoint(IPlacementTestGrain grain, int sampleSize)
        {
            for (var i = 0; i < sampleSize; ++i)
                yield return grain.GetEndpoint().Result;
        }

        private IEnumerable<ActivationId> CollectActivationIds(IPlacementTestGrain grain, int sampleSize)
        {
            for (var i = 0; i < sampleSize; ++i)
                yield return grain.GetActivationId().Result;
        }

        private int ActivationCount(IEnumerable<ActivationId> ids)
        {
            return ids.GroupBy(id => id).Count();
        }

        private int ActivationCount(IPlacementTestGrain grain, int sampleSize)
        {
            return ActivationCount(CollectActivationIds(grain, sampleSize));
        }

        //[TestMethod, TestCategory("Placement"), TestCategory("Nightly")]
        public void LocallyPlacedGrainShouldCreateSpecifiedNumberOfMultipleActivations()
        {
            WaitForLivenessToStabilize();
            logger.Info("********************** Starting the test LocallyPlacedGrainShouldCreateSpecifiedNumberOfMultipleActivations ******************************");
            TestSilosStarted(2);

            // note: this amount should agree with both the specified minimum and maximum in the StatelessWorkerPlacement attribute
            // associated with ILocalPlacementTestGrain.
            const int expected = 10;
            var grain = LocalPlacementTestGrainFactory.GetGrain(0);
            int actual = ActivationCount(grain, expected * 5);
            Assert.AreEqual(expected, actual,
                "A grain instantiated with the local placement strategy should create multiple activations acording to the parameterization of the strategy.");
        }

        [TestMethod, TestCategory("Placement"), TestCategory("Nightly")]
        public async Task LocallyPlacedGrainShouldCreateActivationsOnLocalSilo()
        {
            WaitForLivenessToStabilize();
            logger.Info("********************** Starting the test LocallyPlacedGrainShouldCreateActivationsOnLocalSilo ******************************");
            TestSilosStarted(2);

            const int sampleSize = 5;
            var placement = new StatelessWorkerPlacement(sampleSize);
            var proxy = RandomPlacementTestGrainFactory.GetGrain(-1);
            await proxy.StartLocalGrains(new List<long> { 0 });
            var expected = await proxy.GetEndpoint();
            // locally placed grains are multi-activation and stateless. this means that we have to sample the value of
            // the result, rather than simply ask for it once in order to get a consensus of the result.
            var actual = await proxy.SampleLocalGrainEndpoint(0, sampleSize);
            Assert.IsTrue(actual.All(expected.Equals),
                "A grain instantiated with the local placement strategy should create activations on the local silo.");
        }
    }
}