using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnitTestGrains;

#pragma warning disable 618

namespace UnitTests.MultipleActivations
{
    [TestClass]
    public class StatelessWorkerTests : UnitTestBase
    {
        const int maxLocalActivations = 10;

        public StatelessWorkerTests()
            : base(new Options { StartFreshOrleans = true, StartSecondary = false })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        // TODO: [TestCategory("Nightly")]
        [TestMethod, TestCategory("Failures"), TestCategory("StatelessWorker")]
        public void StatelessWorker()
        {
            IStatelessWorkerGrain grain = StatelessWorkerGrainFactory.GetGrain(0);
            List<Task> promises = new List<Task>();

            for (int i = 0; i < maxLocalActivations; i++)
                promises.Add(grain.LongCall()); //trigger creation of 10 local activations (default MalLocal=10)
            Task.WhenAll(promises).Wait();

            Thread.Sleep(2000); // for just in case

            promises.Clear();
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < 100; i++)
                promises.Add(grain.LongCall()); //send 50 requests to 10 activations
            Task.WhenAll(promises).Wait();

            stopwatch.Stop();

            Assert.IsTrue(stopwatch.Elapsed > TimeSpan.FromSeconds(19.5), "50 requests with a 2 second processing time shouldn't take less than 20 seconds on 10 activations. But it took " + stopwatch.Elapsed);

            promises.Clear();
            for (int i = 0; i < 20; i++)
                promises.Add(grain.GetCallStats()); //trigger creation of 10 local activations (default MalLocal=10)
            Task.WhenAll(promises).Wait();

            HashSet<Guid> guids = new HashSet<Guid>();
            foreach (var promise in promises)
            {
                Tuple<Guid, List<Tuple<DateTime, DateTime>>> response =
                    ((Task<Tuple<Guid, List<Tuple<DateTime, DateTime>>>>) promise).Result;

                if (guids.Contains(response.Item1))
                    continue; // duplicate response from the same activation

                guids.Add(response.Item1);

                logger.Info(" {0}: Activation {1}", guids.Count, response.Item1);
                int count = 1;
                foreach(Tuple<DateTime,DateTime> call in response.Item2)
                    logger.Info("\t{0}: {1} - {2}", count++, call.Item1, call.Item2);
            }
        }
    }
}

#pragma warning restore 618
