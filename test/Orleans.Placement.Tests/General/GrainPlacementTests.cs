using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for various grain placement strategies including random, prefer local, and stateless worker placement.
    /// </summary>
    public class GrainPlacementTests(DefaultClusterFixture fixture) : IClassFixture<DefaultClusterFixture>
    {
        private readonly DefaultClusterFixture _fixture = fixture;

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task VerifyDefaultPlacement()
        {
            var actual = await _fixture.GrainFactory.GetGrain<IDefaultPlacementGrain>(Random.Shared.Next()).GetDefaultPlacement();
            Assert.IsType<ResourceOptimizedPlacement>(actual);
        }

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task RandomlyPlacedGrainShouldPlaceActivationsRandomly()
        {
            var grains =
                Enumerable.Range(0, 20).
                Select(
                    n =>
                        _fixture.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid()));
            List<string> places = new();
            foreach (var grain in grains)
            {
                places.Add(await grain.GetRuntimeInstanceId());
            }

            // consider: it seems like we should check that we get close to a 50/50 split for placement.
            var groups = places.GroupBy(s => s);
            Assert.True(groups.Count() > 1,
                "Grains should be on different silos, but they are on " + Utils.EnumerableToString(places)); // will randomly fail one in a million times if RNG is good :-)
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
            int numGrains = 20;
            var randomGrains =
                Enumerable.Range(0, numGrains).
                    Select(
                        n =>
                            _fixture.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid())).ToList();
            var randomGrainPlaces = new List<string>();
            foreach (var grain in randomGrains)
            {
                randomGrainPlaces.Add(await grain.GetRuntimeInstanceId());
            }

            var preferLocalGrainKeys = new List<Guid>();
            foreach (var grain in randomGrains)
            {
                preferLocalGrainKeys.Add(await grain.StartPreferLocalGrain(grain.GetPrimaryKey()));
            }

            var preferLocalGrainPlaces = new List<string>();
            foreach (var key in preferLocalGrainKeys)
            {
                preferLocalGrainPlaces.Add(await _fixture.GrainFactory.GetGrain<IPreferLocalPlacementTestGrain>(key).GetRuntimeInstanceId());
            }

            // check that every "prefer local grain" was placed on the same silo with its requesting random grain
            foreach(int key in Enumerable.Range(0, numGrains))
            {
                string random = randomGrainPlaces.ElementAt(key);
                string preferLocal = preferLocalGrainPlaces.ElementAt(key);
                Assert.Equal(random, preferLocal);  //"Grains should be on the same silos, but they are on " + random + " and " + preferLocal
            }
        }

        private static async Task<List<string>> CollectActivationIds(IPlacementTestGrain grain, int sampleSize)
        {
            var tasks = new List<Task<string>>(sampleSize);
            for (var i = 0; i < sampleSize; ++i)
            {
                tasks.Add(grain.GetActivationId());
            }

            await Task.WhenAll(tasks);
            return tasks.Select(t => t.Result).ToList();
        }

        private static async Task<int> ActivationCount(IPlacementTestGrain grain, int sampleSize)
        {
            var activations = await CollectActivationIds(grain, sampleSize);
            return activations.Distinct().Count();
        }

        [Fact, TestCategory("Placement"), TestCategory("BVT")]
        public async Task StatelessWorkerShouldCreateSpecifiedActivationCount()
        {
            {
                // note: this amount should agree with both the specified minimum and maximum in the StatelessWorkerPlacement attribute
                // associated with ILocalPlacementTestGrain.
                const int expected = 1;
                var grain = _fixture.GrainFactory.GetGrain<IStatelessWorkerPlacementTestGrain>(Guid.NewGuid());
                int actual = await ActivationCount(grain, expected * 50);
                Assert.True(actual <= expected, $"Created more activations than the specified limit: {actual} > {expected}.");
            }

            {
                const int expected = 2;
                var grain = _fixture.GrainFactory.GetGrain<IOtherStatelessWorkerPlacementTestGrain>(Guid.NewGuid());
                int actual = await ActivationCount(grain, expected * 50);
                Assert.True(actual <= expected, $"Created more activations than the specified limit: {actual} > {expected}.");

            }
        }

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task StatelessWorkerGrainShouldCreateActivationsOnLocalSilo()
        {
            const int sampleSize = 5;
            var placement = new StatelessWorkerPlacement(sampleSize);
            var proxy = _fixture.GrainFactory.GetGrain<IRandomPlacementTestGrain>(Guid.NewGuid());
            await proxy.StartLocalGrains(new List<Guid> { Guid.Empty });
            var expected = await proxy.GetEndpoint();
            // locally placed grains are multi-activation and stateless. this means that we have to sample the value of
            // the result, rather than simply ask for it once in order to get a consensus of the result.
            var actual = await proxy.SampleLocalGrainEndpoint(Guid.Empty, sampleSize);
            Assert.True(actual.All(expected.Equals),
                "A grain instantiated with the local placement strategy should create activations on the local silo.");
        }
    }
}
