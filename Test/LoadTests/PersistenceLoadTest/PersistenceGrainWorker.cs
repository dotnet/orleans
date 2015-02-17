using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LoadTestBase;
using LoadTestGrainInterfaces;
using System.Diagnostics;

namespace Orleans.Tests.Persistence
{
    public class PersistenceGrainWorker : OrleansClientWorkerBase, IPersistenceWorker
    {
        internal static bool Verbose = false;

        private string testName;
        private long numGrains;
        private IPersistenceLoadTestGrain[] grains;
        public TimeSpan AggregateLatency { get; private set; } // sum of all latencies
        private object lockable = new object();

        public void ApplicationInitialize(string name, long nGrains, PartitionKeyType partKeyType)
        {
            this.testName = name;
            this.numGrains = nGrains;
            this.AggregateLatency = TimeSpan.Zero;
            grains = new IPersistenceLoadTestGrain[nGrains];
            PrecreateGrainState(nGrains).Wait();
        }

        protected override async Task IssueRequest(int requestNumber, int threadNumber)
        {
            long grainNumber = requestNumber % numGrains;

            IPersistenceLoadTestGrain grain = grains[grainNumber];

            if (Verbose)
            {
                WriteProgress(
                    "{0}.IssueRequest: Grain #{1} request #{2}",
                    testName,
                    grainNumber,
                    requestNumber);
            }

            Stopwatch sw = Stopwatch.StartNew();
            await grain.DoStateWrite(requestNumber);
            sw.Stop();
            lock (lockable)
            {
                AggregateLatency += sw.Elapsed;
            }
        }

        private Task PrecreateGrainState(long grainCount)
        {
            WriteProgress(
                "{0}.Precreate: starting {1} grains",
                testName,
                grainCount
            );

            this.grains = GenerateGrainRefs(grainCount).ToArray();

            List<Task> promises = new List<Task>();
            foreach (var grain in grains)
            {
                Task t = grain.GetStateValue();
                promises.Add(t);
            }
            return Task.WhenAll(promises);
        }

        private IEnumerable<IPersistenceLoadTestGrain> GenerateGrainRefs(long count)
        {
            for (var i = 0; i < count; ++i)
                yield return PersistenceLoadTestGrainFactory.GetGrain(i);
        }
    }
}