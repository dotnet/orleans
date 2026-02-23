using Orleans.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
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
    /// don't result in multiple activations within a single silo.
    /// </summary>
    public class DuplicateActivationsTests : IClassFixture<DuplicateActivationsTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                // Increase response timeout to accommodate the high load during stress testing
                // This prevents timeouts that could interfere with duplicate activation detection
                hostBuilder.Configure<SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromMinutes(1));
            }
        }


        public DuplicateActivationsTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Stress test for duplicate activation prevention.
        /// Creates 100 runner grains that each make 100 calls to 10 target grains.
        /// This generates 10,000 concurrent requests to just 10 grains, creating
        /// extreme contention that tests the catalog's synchronization mechanisms.
        /// 
        /// If duplicate activations occur, the target grains will detect this and throw exceptions.
        /// The test passes only if all calls complete without any duplicate activation errors.
        /// </summary>
        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public async Task DuplicateActivations()
        {
            const int nRunnerGrains = 100;    // Number of grains making concurrent calls
            const int nTargetGrain = 10;      // Number of target grains (high contention)
            const int startingKey = 1000;     // Starting grain ID for target grains
            const int nCallsToEach = 100;     // Calls each runner makes to each target

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
            promises.Clear();

            // Phase 2: All runners simultaneously blast calls to the same target grains
            // This creates massive concurrent activation pressure on the catalog
            for (int i = 0; i < nRunnerGrains; i++)
            {
                promises.Add(runnerGrains[i].BlastCallNewGrains(nTargetGrain, startingKey, nCallsToEach));
            }

            // If any duplicate activations occur, the grains will detect and report them
            await Task.WhenAll(promises);
        }
    }
}
