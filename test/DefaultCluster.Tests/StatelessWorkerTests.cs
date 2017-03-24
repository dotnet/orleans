using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests.General
{
    public class StatelessWorkerTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly int ExpectedMaxLocalActivations = StatelessWorkerGrain.MaxLocalWorkers; // System.Environment.ProcessorCount;
        private readonly ITestOutputHelper output;

        public StatelessWorkerTests(DefaultClusterFixture fixture, ITestOutputHelper output) : base(fixture)
        {
            this.output = output;
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("StatelessWorker")]
        public async Task StatelessWorkerActivationsPerSiloDoNotExceedMaxLocalWorkersCount()
        {
            var gatewaysCount = HostedCluster.ClientConfiguration.Gateways.Count;
            // do extra calls to trigger activation of ExpectedMaxLocalActivations local activations
            int numberOfCalls = ExpectedMaxLocalActivations * 3 * gatewaysCount; 

            IStatelessWorkerGrain grain = this.GrainFactory.GetGrain<IStatelessWorkerGrain>(GetRandomGrainId());
            List<Task> promises = new List<Task>();

            // warmup
            for (int i = 0; i < gatewaysCount; i++)
                promises.Add(grain.LongCall()); 
            await Task.WhenAll(promises);

            await Task.Delay(2000);

            promises.Clear();
            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < numberOfCalls; i++)
                promises.Add(grain.LongCall());
            await Task.WhenAll(promises);

            stopwatch.Stop();

            promises.Clear();

            var statsTasks = new List<Task<Tuple<Guid, string, List<Tuple<DateTime, DateTime>>>>>();
            for (int i = 0; i < numberOfCalls; i++)
                statsTasks.Add(grain.GetCallStats());  // gather stats
            await Task.WhenAll(promises);

            var responsesPerSilo = statsTasks.Select(t => t.Result).GroupBy(s => s.Item2);
            foreach (var siloGroup in responsesPerSilo)
            {
                var silo = siloGroup.Key;

                HashSet<Guid> activations = new HashSet<Guid>();

                foreach (var response in siloGroup)
                {
                    if (activations.Contains(response.Item1))
                        continue; // duplicate response from the same activation

                    activations.Add(response.Item1);

                    output.WriteLine($"Silo {silo} with {activations.Count} activations: Activation {response.Item1}");
                    int count = 1;
                    foreach (Tuple<DateTime, DateTime> call in response.Item3)
                        output.WriteLine($"\t{count++}: {LogFormatter.PrintDate(call.Item1)} - {LogFormatter.PrintDate(call.Item2)}");
                }

                Assert.True(activations.Count <= ExpectedMaxLocalActivations, $"activations.Count = {activations.Count} in silo {silo} but expected no more than {ExpectedMaxLocalActivations}");
            }
        }

        [SkippableFact(Skip = "Skipping test for now, since there seems to be a bug"), TestCategory("Functional"), TestCategory("StatelessWorker")]
        public async Task StatelessWorkerFastActivationsDontFailInMultiSiloDeployment()
        {
            var gatewaysCount = HostedCluster.ClientConfiguration.Gateways.Count;

            if (gatewaysCount < 2)
            {
                throw new SkipException("This test was created to run with more than 1 gateway. 2 is the default at the time of this writing");
            }

            // do extra calls to trigger activation of ExpectedMaxLocalActivations local activations
            int numberOfCalls = ExpectedMaxLocalActivations * 3 * gatewaysCount;

            IStatelessWorkerGrain grain = this.GrainFactory.GetGrain<IStatelessWorkerGrain>(GetRandomGrainId());
            List<Task> promises = new List<Task>();
            
            for (int i = 0; i < numberOfCalls; i++)
                promises.Add(grain.LongCall());
            await Task.WhenAll(promises);

            // Calls should not have thrown ForwardingFailed exceptions.
        }
    }
}
