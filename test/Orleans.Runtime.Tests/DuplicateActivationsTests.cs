using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.CatalogTests
{
    /// <summary>
    /// Tests the Orleans catalog's ability to prevent duplicate grain activations under high concurrency.
    /// 
    /// Orleans guarantees single activation semantics - each grain ID should have at most one activation
    /// in the cluster at any time. This test stress-tests this guarantee by having multiple runner grains
    /// simultaneously make calls to the same set of target grains.
    /// 
    /// The catalog is responsible for ensuring that concurrent activation requests for the same grain
    /// result in one activation across the cluster.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class DuplicateActivationsTests : IClassFixture<DuplicateActivationsTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
        }

        public DuplicateActivationsTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Stress test for duplicate activation prevention.
        /// Creates 100 runner grains that concurrently call the same 10 target grains.
        /// This generates 1,000 concurrent requests for previously inactive grains,
        /// creating contention in the catalog's activation synchronization.
        /// 
        /// Each activation returns its unique identifier so that the test can verify
        /// that every request for a target grain was handled by the same activation.
        /// </summary>
        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public async Task DuplicateActivations()
        {
            const int nRunnerGrains = 100;    // Number of grains making concurrent calls
            const int nTargetGrain = 10;      // Number of target grains (high contention)
            const int startingKey = 1000;     // Starting grain ID for target grains

            var runnerGrains = new ICatalogTestGrain[nRunnerGrains];

            // Phase 1: Initialize all runner grains
            // Using negative IDs for runners to avoid collision with target grain IDs
            var promises = new List<Task>(nRunnerGrains);
            for (int i = 0; i < nRunnerGrains; i++)
            {
                runnerGrains[i] = this.fixture.GrainFactory.GetGrain<ICatalogTestGrain>(-i);
                promises.Add(runnerGrains[i].Initialize());
            }

            await Task.WhenAll(promises);

            // Phase 2: All runners simultaneously blast calls to the same target grains
            // This creates concurrent activation pressure on the catalog
            using var callBarrier = CatalogTestGrain.ArmConcurrentCallBarrier(nRunnerGrains);
            var participantsReady = callBarrier.WaitForParticipantsAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var activationIdPromises = new List<Task<string[]>>(nRunnerGrains);
            for (int i = 0; i < nRunnerGrains; i++)
            {
                activationIdPromises.Add(runnerGrains[i].GetActivationIds(nTargetGrain, startingKey));
            }

            await participantsReady;
            callBarrier.Release();
            var activationIdsByRunner = await Task.WhenAll(activationIdPromises);
            Assert.All(activationIdsByRunner, activationIds => Assert.Equal(nTargetGrain, activationIds.Length));

            for (int targetIndex = 0; targetIndex < nTargetGrain; targetIndex++)
            {
                var activationIds = new HashSet<string>();
                foreach (var activationIdsForRunner in activationIdsByRunner)
                {
                    activationIds.Add(activationIdsForRunner[targetIndex]);
                }

                Assert.True(
                    activationIds.Count == 1,
                    $"Target grain {startingKey + targetIndex} was handled by {activationIds.Count} activations: {string.Join(", ", activationIds.Order())}");
            }
        }
    }
}
