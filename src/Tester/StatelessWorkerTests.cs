using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Orleans.Runtime;


namespace UnitTests.General
{
    [TestClass]
    public class StatelessWorkerTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly int ExpectedMaxLocalActivations = 1; // System.Environment.ProcessorCount;

        [TestMethod, TestCategory("Functional"), TestCategory("StatelessWorker")]
        public async Task StatelessWorker()
        {
            IStatelessWorkerGrain grain = GrainClient.GrainFactory.GetGrain<IStatelessWorkerGrain>(0);
            List<Task> promises = new List<Task>();

            for (int i = 0; i < ExpectedMaxLocalActivations * 3; i++)
                promises.Add(grain.LongCall()); // trigger activation of ExpectedMaxLocalActivations local activations
            await Task.WhenAll(promises);

            await Task.Delay(2000);

            promises.Clear();
            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < ExpectedMaxLocalActivations * 3; i++)
                promises.Add(grain.LongCall());
            await Task.WhenAll(promises);

            stopwatch.Stop();

            //Assert.IsTrue(stopwatch.Elapsed > TimeSpan.FromSeconds(19.5), "50 requests with a 2 second processing time shouldn't take less than 20 seconds on 10 activations. But it took " + stopwatch.Elapsed);

            promises.Clear();
            for (int i = 0; i < ExpectedMaxLocalActivations * 3; i++)
                promises.Add(grain.GetCallStats());  // gather stats
            await Task.WhenAll(promises);

            HashSet<Guid> activations = new HashSet<Guid>();
            foreach (var promise in promises)
            {
                Tuple<Guid, List<Tuple<DateTime, DateTime>>> response = await (Task<Tuple<Guid, List<Tuple<DateTime, DateTime>>>>)promise;

                if (activations.Contains(response.Item1))
                    continue; // duplicate response from the same activation

                activations.Add(response.Item1);

                logger.Info(" {0}: Activation {1}", activations.Count, response.Item1);
                int count = 1;
                foreach (Tuple<DateTime, DateTime> call in response.Item2)
                    logger.Info("\t{0}: {1} - {2}", count++, TraceLogger.PrintDate(call.Item1), TraceLogger.PrintDate(call.Item2));
            }

            Assert.IsTrue(activations.Count <= ExpectedMaxLocalActivations, "activations.Count = " + activations.Count + " but expected no more than " + ExpectedMaxLocalActivations);
        }
    }
}
