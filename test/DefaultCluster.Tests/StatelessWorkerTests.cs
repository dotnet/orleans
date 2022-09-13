using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
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

        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task StatelessWorkerThrowExceptionConstructor()
        {
            var grain = this.GrainFactory.GetGrain<IStatelessWorkerExceptionGrain>(0);

            for (int i=0; i<100; i++)
            {
                var ex = await Assert.ThrowsAsync<Exception>(() => grain.Ping());
                Assert.Equal("oops", ex.Message);
            }
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task StatelessWorkerActivationsPerSiloDoNotExceedMaxLocalWorkersCount()
        {
            var gatewayOptions = this.Fixture.Client.ServiceProvider.GetRequiredService<IOptions<StaticGatewayListProviderOptions>>();
            var gatewaysCount = gatewayOptions.Value.Gateways.Count;
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

        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task ManyConcurrentInvocationsOnActivationLimitedStatelessWorkerDoesNotFail()
        {
            // Issue #6795: significantly more concurrent invocations than the local worker limit results in too many
            // message forwards. When the issue occurs, this test will throw an exception.

            // We are trying to trigger a race condition and need more than 1 attempt to reliably reproduce the issue.
            for (var attempt = 0; attempt < 100; attempt ++)
            {
                var grain = this.GrainFactory.GetGrain<IStatelessWorkerGrain>(attempt);
                await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => grain.DummyCall()));
            }
        }

        [SkippableFact(Skip = "Skipping test for now, since there seems to be a bug"), TestCategory("Functional"), TestCategory("StatelessWorker")]
        public async Task StatelessWorkerFastActivationsDontFailInMultiSiloDeployment()
        {
            var gatewayOptions = this.Fixture.Client.ServiceProvider.GetRequiredService<IOptions<StaticGatewayListProviderOptions>>();
            var gatewaysCount = gatewayOptions.Value.Gateways.Count;

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
