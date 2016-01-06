using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests
{
    [TestClass]

    public class GrainPlacementTests : UnitTestSiloHost
    {
        public GrainPlacementTests()
            : base(new TestingSiloOptions
                    {
                        StartFreshOrleans = true,
                        SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml")
                    }, new TestingClientOptions
                    {
                        ProxiedGateway = true,
                        Gateways = new List<IPEndPoint>(new IPEndPoint[] { new IPEndPoint(IPAddress.Loopback, 40000), new IPEndPoint(IPAddress.Loopback, 40001) }),
                        PreferedGatewayIndex = -1,
                        ClientConfigFile = new FileInfo("ClientConfigurationForTesting.xml"),
                    })
        {
        }

        [TestCleanup()]
        public void TestCleanup()
        {
            RestartAllAdditionalSilos();
            RestartDefaultSilos();
        }

        [TestMethod, TestCategory("Placement"), TestCategory("Functional")]
        public async Task DefaultPlacementShouldBeRandom()
        {
            await WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test DefaultPlacementShouldBeRandom ******************************");
            TestSilosStarted(2);

            Assert.AreEqual(
                RandomPlacement.Singleton,
                PlacementStrategy.GetDefault(),
                "The default placement strategy is expected to be random.");
        }

        [TestMethod, TestCategory("Placement"), TestCategory("Functional")]
        public async Task RandomlyPlacedGrainShouldPlaceActivationsRandomly()
        {
            await WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test RandomlyPlacedGrainShouldPlaceActivationsRandomly ******************************");
            TestSilosStarted(2);

            logger.Info("********************** TestSilosStarted passed OK. ******************************");

            var placement = RandomPlacement.Singleton;
            var grains =
                Enumerable.Range(0, 20).
                Select(
                    n =>
                        GrainClient.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid()));
            var places = grains.Select(g => g.GetRuntimeInstanceId().Result);
            var placesAsArray = places as string[] ?? places.ToArray();
            // consider: it seems like we should check that we get close to a 50/50 split for placement.
            var groups = placesAsArray.GroupBy(s => s);
            Assert.IsTrue(groups.Count() > 1,
                "Grains should be on different silos, but they are on " + Utils.EnumerableToString(placesAsArray.ToArray())); // will randomly fail one in a million times if RNG is good :-)
        }

        //[TestMethod, TestCategory("Placement"), TestCategory("Functional")]
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

        [TestMethod, TestCategory("Placement"), TestCategory("Functional")]
        public async Task PreferLocalPlacedGrainShouldPlaceActivationsLocally_TwoHops()
        {
            await WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test PreferLocalPlacedGrainShouldPlaceActivationsLocally ******************************");
            TestSilosStarted(2);

            int numGrains = 20;
            var randomGrains =
                Enumerable.Range(0, numGrains).
                    Select(
                        n =>
                            GrainClient.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid())).ToList();
            var randomGrainPlaces = randomGrains.Select(g => g.GetRuntimeInstanceId().Result).ToList();

            var preferLocalGrainKeys =
                randomGrains.
                    Select(
                        (IRandomPlacementTestGrain g) =>
                            g.StartPreferLocalGrain(g.GetPrimaryKey()).Result).ToList();
            var preferLocalGrainPlaces = preferLocalGrainKeys.Select(key => GrainClient.GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(key).GetRuntimeInstanceId().Result).ToList();

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

        private IEnumerable<string> CollectActivationIds(IPlacementTestGrain grain, int sampleSize)
        {
            for (var i = 0; i < sampleSize; ++i)
                yield return grain.GetActivationId().Result;
        }

        private int ActivationCount(IEnumerable<string> ids)
        {
            return ids.GroupBy(id => id).Count();
        }

        private int ActivationCount(IPlacementTestGrain grain, int sampleSize)
        {
            return ActivationCount(CollectActivationIds(grain, sampleSize));
        }

        //[TestMethod, TestCategory("Placement"), TestCategory("Functional")]
        public async Task LocallyPlacedGrainShouldCreateSpecifiedNumberOfMultipleActivations()
        {
            await WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test LocallyPlacedGrainShouldCreateSpecifiedNumberOfMultipleActivations ******************************");
            TestSilosStarted(2);

            // note: this amount should agree with both the specified minimum and maximum in the StatelessWorkerPlacement attribute
            // associated with ILocalPlacementTestGrain.
            const int expected = 10;
            var grain = GrainClient.GrainFactory.GetGrain<ILocalPlacementTestGrain>(Guid.Empty);
            int actual = ActivationCount(grain, expected * 5);
            Assert.AreEqual(expected, actual,
                "A grain instantiated with the local placement strategy should create multiple activations acording to the parameterization of the strategy.");
        }

        [TestMethod, TestCategory("Placement"), TestCategory("Functional")]
        public async Task LocallyPlacedGrainShouldCreateActivationsOnLocalSilo()
        {
            await WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test LocallyPlacedGrainShouldCreateActivationsOnLocalSilo ******************************");
            TestSilosStarted(2);

            const int sampleSize = 5;
            var placement = new StatelessWorkerPlacement(sampleSize);
            var proxy = GrainClient.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid());
            await proxy.StartLocalGrains(new List<Guid> { Guid.Empty });
            var expected = await proxy.GetEndpoint();
            // locally placed grains are multi-activation and stateless. this means that we have to sample the value of
            // the result, rather than simply ask for it once in order to get a consensus of the result.
            var actual = await proxy.SampleLocalGrainEndpoint(Guid.Empty, sampleSize);
            Assert.IsTrue(actual.All(expected.Equals),
                "A grain instantiated with the local placement strategy should create activations on the local silo.");
        }
    }
}