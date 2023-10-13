using Orleans.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CatalogTests
{
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
                hostBuilder.Configure<SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromMinutes(1));
            }
        }


        public DuplicateActivationsTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public async Task DuplicateActivations()
        {
            const int nRunnerGrains = 100;
            const int nTargetGrain = 10;
            const int startingKey = 1000;
            const int nCallsToEach = 100;

            var runnerGrains = new ICatalogTestGrain[nRunnerGrains];

            var promises = new List<Task>(nRunnerGrains);
            for (int i = 0; i < nRunnerGrains; i++)
            {
                runnerGrains[i] = this.fixture.GrainFactory.GetGrain<ICatalogTestGrain>(-i);
                promises.Add(runnerGrains[i].Initialize());
            }

            await Task.WhenAll(promises);
            promises.Clear();

            for (int i = 0; i < nRunnerGrains; i++)
            {
                promises.Add(runnerGrains[i].BlastCallNewGrains(nTargetGrain, startingKey, nCallsToEach));
            }

            await Task.WhenAll(promises);
        }
    }
}
