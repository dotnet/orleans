using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming"), TestCategory("AzureStorage")]
    [TestSuite("Functional")]
    [TestProvider("AzureStorage")]
    [TestArea("Streaming")]
    public class DelayedQueueRebalancingTests : TestClusterPerTest
    {
        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private static readonly TimeSpan SILO_IMMATURE_PERIOD = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan AGENT_STATE_TIMEOUT = SILO_IMMATURE_PERIOD + TimeSpan.FromSeconds(20);
        private const int queueCount = 8;
        private StreamQueueDiagnosticObserver diagnosticObserver = null!;

        public override async ValueTask InitializeAsync()
        {
            diagnosticObserver = StreamQueueDiagnosticObserver.Create(adapterName);
            try
            {
                await base.InitializeAsync();
                if (!PreconditionsMet)
                {
                    return;
                }
            }
            catch
            {
                diagnosticObserver.Dispose();
                throw;
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();

            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        }

        private class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<StaticGatewayListProviderOptions>(options => options.Gateways = options.Gateways.Take(1).ToList());
            }
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams(adapterName, b =>
                    {
                        b.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
                        b.UseDynamicClusterConfigDeploymentBalancer(SILO_IMMATURE_PERIOD);
                    })
                    .Configure<StaticClusterDeploymentOptions>(op =>
                    {
                        op.SiloNames = new List<string>() { "Primary", "Secondary_1", "Secondary_2", "Secondary_3" };
                    });
                hostBuilder.AddMemoryGrainStorage("PubSubStore");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (!PreconditionsMet)
            {
                return;
            }

            var cluster = this.HostedCluster;
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                diagnosticObserver?.Dispose();
                if (cluster is not null)
                {
                    try
                    {
                        TestUtils.CheckForAzureStorage();
                        await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                            AzureQueueUtilities.GenerateQueueNames(cluster.Options.ClusterId, queueCount),
                            new AzureQueueOptions().ConfigureTestDefaults());
                    }
                    catch (Xunit.Sdk.SkipException) { }
                }
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task DelayedQueueRebalancingTests_1()
        {
            await WaitForAgentsState(
                2,
                "1",
                TestContext.Current.CancellationToken,
                includeHistory: true);

            await WaitForAgentsState(4, "2", TestContext.Current.CancellationToken);
        }

        [Fact, TestCategory("Functional")]
        public async Task DelayedQueueRebalancingTests_2()
        {
            await WaitForAgentsState(
                2,
                "1",
                TestContext.Current.CancellationToken,
                includeHistory: true);

            var existingSilos = this.HostedCluster.GetActiveSilos().Select(silo => silo.SiloAddress).ToArray();
            var addedSilos = await this.HostedCluster.StartAdditionalSilosAsync(
                2,
                true,
                TestContext.Current.CancellationToken);
            var addedSiloAddresses = addedSilos.Select(silo => silo.SiloAddress).ToArray();
            var activeSiloAddresses = this.HostedCluster.GetActiveSilos().Select(silo => silo.SiloAddress).ToArray();

            await WaitForAgentsState(2, "2", TestContext.Current.CancellationToken);

            await diagnosticObserver.WaitForLocalMaturityAsync(
                activeSiloAddresses,
                AGENT_STATE_TIMEOUT,
                "3",
                TestContext.Current.CancellationToken);
            await diagnosticObserver.WaitForRemoteMaturityAsync(
                existingSilos,
                addedSiloAddresses,
                AGENT_STATE_TIMEOUT,
                "3",
                TestContext.Current.CancellationToken);
            await WaitForAgentsState(2, "3", TestContext.Current.CancellationToken);
        }

        private Task WaitForAgentsState(
            int numExpectedAgentsPerSilo,
            string callContext,
            CancellationToken cancellationToken,
            bool includeHistory = false)
        {
            var activeSilos = this.HostedCluster.GetActiveSilos().Select(silo => silo.SiloAddress).ToArray();
            return diagnosticObserver.WaitForAgentsStateAsync(
                activeSilos,
                numExpectedAgentsPerSilo,
                includeHistory,
                AGENT_STATE_TIMEOUT,
                callContext,
                cancellationToken);
        }

        private sealed class StreamQueueDiagnosticObserver : IDisposable, IObserver<StreamingEvents.StreamingEvent>
        {
            private readonly string streamProvider;
            private readonly IDisposable subscription;
            private readonly object lockObj = new();
            private readonly Dictionary<SiloAddress, StreamingEvents.PullingAgentManagerState> agentStates = [];
            private readonly HashSet<(SiloAddress SiloAddress, int RunningAgents)> observedAgentStates = [];
            private readonly HashSet<SiloAddress> locallyMaturedSilos = [];
            private readonly HashSet<(SiloAddress LocalSilo, SiloAddress MaturedSilo)> remotelyMaturedSilos = [];
            private readonly List<ConditionWaiter> waiters = [];

            private StreamQueueDiagnosticObserver(string streamProvider)
            {
                this.streamProvider = streamProvider;
                subscription = StreamingEvents.AllEvents.Subscribe(this);
            }

            public static StreamQueueDiagnosticObserver Create(string streamProvider)
            {
                return new StreamQueueDiagnosticObserver(streamProvider);
            }

            public Task WaitForAgentsStateAsync(
                SiloAddress[] expectedSilos,
                int expectedAgentsPerSilo,
                bool includeHistory,
                TimeSpan timeout,
                string callContext,
                CancellationToken cancellationToken)
            {
                return WaitUntilAsync(
                    () => HasAgentState(expectedSilos, expectedAgentsPerSilo, includeHistory),
                    timeout,
                    () => $"Call {callContext}: expected silos {Utils.EnumerableToString(expectedSilos)} to have {expectedAgentsPerSilo} agents each, got {FormatAgentStates(expectedSilos)}.",
                    cancellationToken);
            }

            public Task WaitForLocalMaturityAsync(
                SiloAddress[] siloAddresses,
                TimeSpan timeout,
                string callContext,
                CancellationToken cancellationToken)
            {
                return WaitUntilAsync(
                    () => siloAddresses.All(locallyMaturedSilos.Contains),
                    timeout,
                    () => $"Call {callContext}: timed out waiting for local queue balancer maturity on silos {Utils.EnumerableToString(siloAddresses)}. Matured silos: {Utils.EnumerableToString(locallyMaturedSilos)}.",
                    cancellationToken);
            }

            public Task WaitForRemoteMaturityAsync(
                SiloAddress[] localSilos,
                SiloAddress[] maturedSilos,
                TimeSpan timeout,
                string callContext,
                CancellationToken cancellationToken)
            {
                var expectedMaturities =
                    (from localSilo in localSilos
                     from maturedSilo in maturedSilos
                     select (LocalSilo: localSilo, MaturedSilo: maturedSilo)).ToArray();

                return WaitUntilAsync(
                    () => expectedMaturities.All(remotelyMaturedSilos.Contains),
                    timeout,
                    () => $"Call {callContext}: timed out waiting for remote queue balancer maturity. Expected: {FormatMaturities(expectedMaturities)}. Completed: {FormatMaturities(remotelyMaturedSilos)}.",
                    cancellationToken);
            }

            public void OnNext(StreamingEvents.StreamingEvent value)
            {
                if (value.StreamProvider != streamProvider)
                {
                    return;
                }

                lock (lockObj)
                {
                    switch (value)
                    {
                        case StreamingEvents.PullingAgentManagerState state when state.SiloAddress is not null:
                            agentStates[state.SiloAddress] = state;
                            observedAgentStates.Add((state.SiloAddress, state.RunningAgents));
                            break;
                        case StreamingEvents.QueueBalancerMaturityCompleted maturity when maturity.SiloAddress is not null:
                            if (maturity.IsLocalSilo)
                            {
                                locallyMaturedSilos.Add(maturity.SiloAddress);
                            }
                            else
                            {
                                remotelyMaturedSilos.Add((maturity.SiloAddress, maturity.MaturedSiloAddress));
                            }
                            break;
                    }

                    SignalWaiters();
                }
            }

            public void OnError(Exception error)
            {
            }

            public void OnCompleted()
            {
            }

            public void Dispose()
            {
                subscription.Dispose();
            }

            private bool HasAgentState(SiloAddress[] expectedSilos, int expectedAgentsPerSilo, bool includeHistory)
            {
                return expectedSilos.All(silo =>
                    includeHistory
                        ? observedAgentStates.Contains((silo, expectedAgentsPerSilo))
                        : agentStates.TryGetValue(silo, out var state) && state.RunningAgents == expectedAgentsPerSilo);
            }

            private Task WaitUntilAsync(
                Func<bool> predicate,
                TimeSpan timeout,
                Func<string> timeoutMessage,
                CancellationToken cancellationToken)
            {
                lock (lockObj)
                {
                    if (predicate())
                    {
                        return Task.CompletedTask;
                    }

                    var waiter = new ConditionWaiter(predicate);
                    waiters.Add(waiter);
                    return WaitWithTimeoutAsync(
                        waiter,
                        timeout,
                        timeoutMessage,
                        cancellationToken);
                }
            }

            private async Task WaitWithTimeoutAsync(
                ConditionWaiter waiter,
                TimeSpan timeout,
                Func<string> timeoutMessage,
                CancellationToken cancellationToken)
            {
                try
                {
                    await waiter.Task.WaitAsync(timeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    string message;
                    lock (lockObj)
                    {
                        waiters.Remove(waiter);
                        message = timeoutMessage();
                    }

                    throw new TimeoutException(message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (lockObj)
                    {
                        waiters.Remove(waiter);
                    }

                    throw;
                }
            }

            private void SignalWaiters()
            {
                for (var i = waiters.Count - 1; i >= 0; i--)
                {
                    if (waiters[i].TryComplete())
                    {
                        waiters.RemoveAt(i);
                    }
                }
            }

            private string FormatAgentStates(SiloAddress[] expectedSilos)
            {
                var states = expectedSilos.Select(silo => agentStates.TryGetValue(silo, out var state) ? $"{silo}: {state.RunningAgents}" : $"{silo}: <missing>");
                return $"{agentStates.Count} silos with agents {Utils.EnumerableToString(states)}";
            }

            private static string FormatMaturities(IEnumerable<(SiloAddress LocalSilo, SiloAddress MaturedSilo)> maturities)
            {
                return Utils.EnumerableToString(maturities.Select(maturity => $"{maturity.LocalSilo}->{maturity.MaturedSilo}"));
            }

            private sealed class ConditionWaiter(Func<bool> predicate)
            {
                private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

                public Task Task => completion.Task;

                public bool TryComplete()
                {
                    return predicate() && completion.TrySetResult();
                }
            }
        }
    }
}
