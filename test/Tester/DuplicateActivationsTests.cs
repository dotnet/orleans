using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
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
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.Globals.ResponseTimeout = TimeSpan.FromMinutes(1);
                options.ClusterConfiguration.ApplyToAllNodes(nodeConfig => nodeConfig.MaxActiveThreads = 1);
                return new TestCluster(options);
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
