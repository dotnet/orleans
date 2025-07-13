using System.Diagnostics;
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
    /// <summary>
    /// Tests for Orleans Stateless Worker grains.
    /// Stateless workers are a special grain type that can have multiple activations
    /// per silo for parallel processing. They're ideal for functional operations
    /// that don't maintain state between calls. The [StatelessWorker] attribute
    /// controls the maximum number of local activations, enabling horizontal
    /// scaling within a silo for CPU-bound or I/O-bound operations.
    /// </summary>
    public class StatelessWorkerTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly int ExpectedMaxLocalActivations = StatelessWorkerGrain.MaxLocalWorkers; // System.Environment.ProcessorCount;
        private readonly ITestOutputHelper output;

        public StatelessWorkerTests(DefaultClusterFixture fixture, ITestOutputHelper output) : base(fixture)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests stateless worker behavior when constructor throws an exception.
        /// Verifies that construction failures are properly reported to callers
        /// and that the system remains stable even when workers fail to instantiate.
        /// This ensures robust error handling for worker initialization.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task StatelessWorkerThrowExceptionConstructor()
        {
            var grain = this.GrainFactory.GetGrain<IStatelessWorkerExceptionGrain>(0);

            for (int i = 0; i < 5; i++)
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Ping());
                Assert.Contains("Failed to create an instance of grain type", ex.Message);
                var iex = Assert.IsType<Exception>(ex.InnerException);
                Assert.Equal("oops", iex.Message);
            }
        }

        /// <summary>
        /// Verifies that stateless worker activations respect the maximum limit per silo.
        /// Tests that when multiple concurrent requests are made to a stateless worker,
        /// the number of local activations doesn't exceed the configured maximum.
        /// This ensures the [StatelessWorker(maxLocalWorkers)] attribute is enforced,
        /// preventing resource exhaustion from unlimited parallelism.
        /// </summary>
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
            await Task.WhenAll(statsTasks);

#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
            var responsesPerSilo = statsTasks.Select(t => t.Result).GroupBy(s => s.Item2);
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
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

        /// <summary>
        /// Tests system stability under high concurrency with limited worker activations.
        /// Verifies that when concurrent invocations significantly exceed the local
        /// worker limit, the system handles message forwarding correctly without
        /// failures. This addresses issue #6795 regarding excessive message forwards
        /// under high concurrency.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task ManyConcurrentInvocationsOnActivationLimitedStatelessWorkerDoesNotFail()
        {
            // Issue #6795: significantly more concurrent invocations than the local worker limit results in too many
            // message forwards. When the issue occurs, this test will throw an exception.

            // We are trying to trigger a race condition and need more than 1 attempt to reliably reproduce the issue.
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var grain = this.GrainFactory.GetGrain<IStatelessWorkerGrain>(attempt);
                await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => grain.DummyCall()));
            }
        }

        /// <summary>
        /// Tests stateless worker behavior in multi-silo deployments.
        /// Verifies that rapid concurrent activations across multiple silos
        /// don't cause forwarding failures. This ensures load distribution
        /// works correctly when workers are spread across the cluster.
        /// NOTE: Currently skipped due to a known issue with forwarding.
        /// </summary>
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

        /// <summary>
        /// Tests that stateless workers can use [MayInterleave] for specific methods.
        /// Verifies that methods marked with MayInterleave can process concurrently
        /// even within a single activation, allowing fine-grained concurrency control
        /// for stateless workers beyond the default turn-based model.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task StatelessWorker_DoesNotThrow_IfMarkedWithMayInterleave()
        {
            var grain = GrainFactory.GetGrain<IStatelessWorkerWithMayInterleaveGrain>(0);
            var observer = new CallbackObserver();
            var reference = GrainFactory.CreateObjectReference<ICallbackGrainObserver>(observer);
            var task = grain.GoFast(reference);
            await observer.OnCallback.WaitAsync(TimeSpan.FromSeconds(10));
            observer.Signal();
            await task;
        }

        /// <summary>
        /// Tests that MayInterleave predicates are properly evaluated for stateless workers.
        /// Verifies that when the MayInterleave predicate returns false, calls are
        /// not interleaved and execute sequentially, maintaining Orleans' turn-based
        /// concurrency for those specific calls even in stateless workers.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task StatelessWorker_ShouldNotInterleaveCalls_IfMayInterleavePredicatedDoesntMatch()
        {
            var grain = GrainFactory.GetGrain<IStatelessWorkerWithMayInterleaveGrain>(0);

            List<CallbackObserver> callbacks = [new(), new(), new()];
            var callbackReferences = callbacks.Select(c => GrainFactory.CreateObjectReference<ICallbackGrainObserver>(c)).ToList();
            List<Task> completions = [grain.GoSlow(callbackReferences[0]), grain.GoSlow(callbackReferences[1]), grain.GoSlow(callbackReferences[2])];
            var callbackSignals = callbacks.Select(c => c.OnCallback).ToList();

            var triggered = await Task.WhenAny(callbackSignals).WaitAsync(TimeSpan.FromSeconds(10));
            callbackSignals.Remove(triggered);
            await Assert.ThrowsAsync<TimeoutException>(async () => await Task.WhenAny(callbackSignals).WaitAsync(TimeSpan.FromSeconds(5)));
            callbacks.ForEach(c => c.Signal());
            await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Tests that MayInterleave allows concurrent execution when predicate matches.
        /// Verifies that when the MayInterleave predicate returns true, multiple calls
        /// can execute concurrently within the same stateless worker activation,
        /// enabling maximum parallelism for suitable operations.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("StatelessWorker")]
        public async Task StatelessWorker_ShouldInterleaveCalls_IfMayInterleavePredicatedMatches()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var grain = GrainFactory.GetGrain<IStatelessWorkerWithMayInterleaveGrain>(0);

            List<CallbackObserver> callbacks = [new(), new(), new()];
            var callbackReferences = callbacks.Select(c => GrainFactory.CreateObjectReference<ICallbackGrainObserver>(c)).ToList();
            var completion = Task.WhenAll(grain.GoFast(callbackReferences[0]), grain.GoFast(callbackReferences[1]), grain.GoFast(callbackReferences[2]));

            // Wait for all callbacks to be triggered simultaneously, giving up if they don't signal before the timeout.
            await Task.WhenAll(callbacks.Select(c => c.OnCallback)).WaitAsync(cts.Token);
            callbacks.ForEach(c => c.Signal());

            await completion;
        }

        private sealed class CallbackObserver : ICallbackGrainObserver
        {
            private readonly TaskCompletionSource _waitAsyncTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _wasCalledTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Signal() => _waitAsyncTcs.TrySetResult();
            public Task OnCallback => _wasCalledTcs.Task;
            async Task ICallbackGrainObserver.WaitAsync()
            {
                _wasCalledTcs.TrySetResult();
                await _waitAsyncTcs.Task;
            }
        }
    }
}
