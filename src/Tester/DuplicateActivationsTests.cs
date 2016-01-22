using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.CatalogTests
{
    [TestClass]
    public class DuplicateActivationsTests : HostedTestClusterPerFixture
    {
        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(new TestingSiloOptions
            {
                AdjustConfig = config =>
                {
                    foreach (var nodeConfig in config.Overrides.Values)
                    {
                        nodeConfig.MaxActiveThreads = 1;
                    }
                },
            });
        }

        [TestMethod, TestCategory("Catalog"), TestCategory("Functional")]
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
