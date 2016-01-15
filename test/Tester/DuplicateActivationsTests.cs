using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Tester;
using Xunit;

namespace UnitTests.CatalogTests
{
    public class DuplicateActivationsTestsFixture : BaseClusterFixture
    {
        public DuplicateActivationsTestsFixture()
        : base(new TestingSiloHost(new TestingSiloOptions
            {
                AdjustConfig = config =>
                {
                    foreach (var nodeConfig in config.Overrides.Values)
                    {
                        nodeConfig.MaxActiveThreads = 1;
                    }
                },
            }))
        {
        }
    }

    public class DuplicateActivationsTests : IClassFixture<DuplicateActivationsTestsFixture>
    {

        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public void DuplicateActivations()
        {
            const int nRunnerGrains = 100;
            const int nTargetGRain = 10;
            const int startingKey = 1000;
            const int nCallsToEach = 100;

            var runnerGrains = new ICatalogTestGrain[nRunnerGrains];

            var promises = new List<Task>();
            for (int i = 0; i < nRunnerGrains; i++)
            {
                runnerGrains[i] = GrainClient.GrainFactory.GetGrain<ICatalogTestGrain>(i.ToString(CultureInfo.InvariantCulture));
                promises.Add(runnerGrains[i].Initialize());
            }

            Task.WhenAll(promises).Wait();
            promises.Clear();

            for (int i = 0; i < nRunnerGrains; i++)
            {
                promises.Add(runnerGrains[i].BlastCallNewGrains(nTargetGRain, startingKey, nCallsToEach));
            }

            Task.WhenAll(promises).Wait();
        }
    }
}
