using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using Orleans;
using UnitTestGrains;


namespace UnitTests.CatalogTests
{
    [TestClass]
    public class DuplicateActivationsTests : UnitTestBase
    {

        public DuplicateActivationsTests()
            : base(new Options {MaxActiveThreads = 1})
        {

        }

        [TestMethod, TestCategory("Catalog"), TestCategory("Nightly")]
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
                runnerGrains[i] = CatalogTestGrainFactory.GetGrain(i.ToString(CultureInfo.InvariantCulture));
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
