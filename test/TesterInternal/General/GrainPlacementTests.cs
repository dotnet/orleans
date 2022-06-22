using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    public class GrainPlacementTests : TestClusterPerTest
    {
        private readonly ITestOutputHelper output;

        public GrainPlacementTests(ITestOutputHelper output)
        {
            this.output = output;
            output.WriteLine("GrainPlacementTests - constructor");
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorageAsDefault();
            }
        }


        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task DefaultPlacementShouldBeRandom()
        {
            logger.LogInformation("********************** Starting the test DefaultPlacementShouldBeRandom ******************************");
            TestSilosStarted(2);

            var actual = await GrainFactory.GetGrain<IDefaultPlacementGrain>(GetRandomGrainId()).GetDefaultPlacement();
            Assert.IsType<RandomPlacement>(actual);
        }

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task RandomlyPlacedGrainShouldPlaceActivationsRandomly()
        {
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            logger.LogInformation("********************** Starting the test RandomlyPlacedGrainShouldPlaceActivationsRandomly ******************************");
            TestSilosStarted(2);

            logger.LogInformation("********************** TestSilosStarted passed OK. ******************************");
            
            var grains =
                Enumerable.Range(0, 20).
                Select(
                    n =>
                        this.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid()));
            var places = grains.Select(g => g.GetRuntimeInstanceId().Result);
            var placesAsArray = places as string[] ?? places.ToArray();
            // consider: it seems like we should check that we get close to a 50/50 split for placement.
            var groups = placesAsArray.GroupBy(s => s);
            Assert.True(groups.Count() > 1,
                "Grains should be on different silos, but they are on " + Utils.EnumerableToString(placesAsArray.ToArray())); // will randomly fail one in a million times if RNG is good :-)
        }

        //[Fact, TestCategory("Placement"), TestCategory("Functional")]
        //public void PreferLocalPlacedGrainShouldPlaceActivationsLocally_OneHop()
        //{
        //    HostedCluster.WaitForLivenessToStabilize();
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

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task PreferLocalPlacedGrainShouldPlaceActivationsLocally_TwoHops()
        {
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            logger.LogInformation("********************** Starting the test PreferLocalPlacedGrainShouldPlaceActivationsLocally ******************************");
            TestSilosStarted(2);

            int numGrains = 20;
            var randomGrains =
                Enumerable.Range(0, numGrains).
                    Select(
                        n =>
                            this.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid())).ToList();
            var randomGrainPlaces = randomGrains.Select(g => g.GetRuntimeInstanceId().Result).ToList();

            var preferLocalGrainKeys =
                randomGrains.
                    Select(
                        (IRandomPlacementTestGrain g) =>
                            g.StartPreferLocalGrain(g.GetPrimaryKey()).Result).ToList();
            var preferLocalGrainPlaces = preferLocalGrainKeys.Select(key => this.GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(key).GetRuntimeInstanceId().Result).ToList();

            // check that every "prefer local grain" was placed on the same silo with its requesting random grain
            foreach(int key in Enumerable.Range(0, numGrains))
            {
                string random = randomGrainPlaces.ElementAt(key);
                string preferLocal = preferLocalGrainPlaces.ElementAt(key);
                Assert.Equal(random, preferLocal);  //"Grains should be on the same silos, but they are on " + random + " and " + preferLocal
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

        //[Fact, TestCategory("Placement"), TestCategory("Functional")]
        /*public async Task LocallyPlacedGrainShouldCreateSpecifiedNumberOfMultipleActivations()
        {
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            logger.Info("********************** Starting the test LocallyPlacedGrainShouldCreateSpecifiedNumberOfMultipleActivations ******************************");
            TestSilosStarted(2);

            // note: this amount should agree with both the specified minimum and maximum in the StatelessWorkerPlacement attribute
            // associated with ILocalPlacementTestGrain.
            const int expected = 10;
            var grain = this.GrainFactory.GetGrain<ILocalPlacementTestGrain>(Guid.Empty);
            int actual = ActivationCount(grain, expected * 5);
            Assert.Equal(expected, actual);  //"A grain instantiated with the local placement strategy should create multiple activations acording to the parameterization of the strategy."
        }*/

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task LocallyPlacedGrainShouldCreateActivationsOnLocalSilo()
        {
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            logger.LogInformation("********************** Starting the test LocallyPlacedGrainShouldCreateActivationsOnLocalSilo ******************************");
            TestSilosStarted(2);

            const int sampleSize = 5;
            var placement = new StatelessWorkerPlacement(sampleSize);
            var proxy = this.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid());
            await proxy.StartLocalGrains(new List<Guid> { Guid.Empty });
            var expected = await proxy.GetEndpoint();
            // locally placed grains are multi-activation and stateless. this means that we have to sample the value of
            // the result, rather than simply ask for it once in order to get a consensus of the result.
            var actual = await proxy.SampleLocalGrainEndpoint(Guid.Empty, sampleSize);
            Assert.True(actual.All(expected.Equals),
                "A grain instantiated with the local placement strategy should create activations on the local silo.");
        }

        [Theory(Skip = "Repo test case for gateway silo connection issue #1859")]
        [InlineData("Primary")]
        [InlineData("Secondary")]
        [TestCategory("BVT"), TestCategory("Placement")]
        public async Task PreferLocalPlacementGrain_ShouldMigrateWhenHostSiloKilled(string value)
        {
            await HostedCluster.WaitForLivenessToStabilizeAsync();
            output.WriteLine("******************** Starting test ({0}) ********************", value);
            TestSilosStarted(2);

            foreach (SiloHandle silo in HostedCluster.GetActiveSilos())
            {
                output.WriteLine(
                    "Silo {0} : Address = {1} Proxy gateway: {2}",
                    silo.Name, silo.SiloAddress, silo.GatewayAddress);
            }

            IPEndPoint targetSilo;
            if (value == "Primary")
            {
                targetSilo = HostedCluster.Primary.SiloAddress.Endpoint;
            }
            else
            {
                targetSilo = HostedCluster.SecondarySilos.First().SiloAddress.Endpoint;
            }
            Guid proxyKey;
            IRandomPlacementTestGrain proxy;
            IPEndPoint expected;
            do
            {
                proxyKey = Guid.NewGuid();
                proxy = GrainFactory.GetGrain<IRandomPlacementTestGrain>(proxyKey);
                expected = await proxy.GetEndpoint();
            } while (!targetSilo.Equals(expected));
            output.WriteLine("Proxy grain was originally located on silo {0}", expected);

            Guid grainKey = proxyKey;
            await proxy.StartPreferLocalGrain(grainKey);
            IPreferLocalPlacementTestGrain grain = GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(grainKey);
            IPEndPoint actual = await grain.GetEndpoint();
            output.WriteLine("PreferLocalPlacement grain was originally located on silo {0}", actual);
            Assert.Equal(expected, actual);  // "PreferLocalPlacement strategy should create activations on the local silo."

            SiloHandle siloToKill = HostedCluster.GetActiveSilos().First(s => s.SiloAddress.Endpoint.Equals(expected));
            output.WriteLine("Killing silo {0} hosting locally placed grain", siloToKill);
            await HostedCluster.StopSiloAsync(siloToKill);

            IPEndPoint newActual = await grain.GetEndpoint();
            output.WriteLine("PreferLocalPlacement grain was recreated on silo {0}", newActual);
            Assert.NotEqual(expected, newActual);  // "PreferLocalPlacement strategy should recreate activations on other silo if local fails."
        }

        [Theory(Skip = "Repo test case for gateway silo connection issue #1859")]
        [InlineData("Primary")]
        [InlineData("Secondary")]
        [TestCategory("BVT"), TestCategory("Placement")]
        public async Task PreferLocalPlacementGrain_ShouldNotMigrateWhenOtherSiloKilled(string value)
        {
            await HostedCluster.WaitForLivenessToStabilizeAsync();
            output.WriteLine("******************** Starting test ({0}) ********************", value);
            TestSilosStarted(2);

            foreach (SiloHandle silo in HostedCluster.GetActiveSilos())
            {
                output.WriteLine(
                    "Silo {0} : Address = {1} Proxy gateway: {2}",
                    silo.Name, silo.SiloAddress, silo.GatewayAddress);
            }

            IPEndPoint targetSilo;
            if (value == "Primary")
            {
                targetSilo = HostedCluster.Primary.SiloAddress.Endpoint;
            }
            else
            {
                targetSilo = HostedCluster.SecondarySilos.First().SiloAddress.Endpoint;
            }
            Guid proxyKey;
            IRandomPlacementTestGrain proxy;
            IPEndPoint expected;
            do
            {
                proxyKey = Guid.NewGuid();
                proxy = GrainFactory.GetGrain<IRandomPlacementTestGrain>(proxyKey);
                expected = await proxy.GetEndpoint();
            } while (!targetSilo.Equals(expected));
            output.WriteLine("Proxy grain was originally located on silo {0}", expected);

            Guid grainKey = proxyKey;
            await proxy.StartPreferLocalGrain(grainKey);
            IPreferLocalPlacementTestGrain grain = GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(grainKey);
            IPEndPoint actual = await grain.GetEndpoint();
            output.WriteLine("PreferLocalPlacement grain was originally located on silo {0}", actual);
            Assert.Equal(expected, actual);  // "PreferLocalPlacement strategy should create activations on the local silo."

            SiloHandle siloToKill = HostedCluster.GetActiveSilos().First(s => !s.SiloAddress.Endpoint.Equals(expected));
            output.WriteLine("Killing other silo {0} not hosting locally placed grain", siloToKill);
            await HostedCluster.StopSiloAsync(siloToKill);

            IPEndPoint newActual = await grain.GetEndpoint();
            output.WriteLine("PreferLocalPlacement grain is now located on silo {0}", newActual);
            Assert.Equal(expected, newActual);  // "PreferLocalPlacement strategy should not move activations when other non-hosting silo fails."
        }

        private void TestSilosStarted(int expected)
        {
            IManagementGrain mgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);

            Dictionary<SiloAddress, SiloStatus> statuses = mgmtGrain.GetHosts(onlyActive: true).Result;
            foreach (var pair in statuses)
            {
                logger.LogInformation("       ######## Silo {SiloAddress}, status: {Status}", pair.Key, pair.Value);
                Assert.Equal(
                    SiloStatus.Active,
                    pair.Value);
            }
            Assert.Equal(expected, statuses.Count);
        }
    }
}
