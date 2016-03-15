using System;
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
    public class DuplicateActivationsTests : IClassFixture<DuplicateActivationsTests.Fixture>
    {
        private class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartPrimary = true,
                    StartSecondary = true,
                    AdjustConfig = config =>
                    {
                        config.Globals.ResponseTimeout = TimeSpan.FromSeconds(45);
                        foreach (var nodeConfig in config.Overrides.Values)
                        {
                            nodeConfig.MaxActiveThreads = 1;
                        }
                    },
                });
            }
        }

        [Fact, TestCategory("Catalog"), TestCategory("Functional")]
        public async Task DuplicateActivations()
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

            await Task.WhenAll(promises);
            promises.Clear();

            for (int i = 0; i < nRunnerGrains; i++)
            {
                promises.Add(runnerGrains[i].BlastCallNewGrains(nTargetGRain, startingKey, nCallsToEach));
            }

            await Task.WhenAll(promises);
        }
    }
}
