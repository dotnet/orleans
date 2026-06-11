using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.Diagnostics;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Lease
{
    /// <summary>
    /// Tests for lease-based queue balancer functionality in Azure Storage, including auto-scaling and node failure scenarios.
    /// </summary>
    [TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("Lease")]
    public class LeaseBasedQueueBalancerTests : TestClusterPerTest
    {
        private const string StreamProviderName = "MemoryStreamProvider";
        private static readonly int totalQueueCount = 6;
        private static readonly short siloCount = 4;

        // Azure Blob leases must be at least 15 seconds, so keep the timeout long enough for lease expiry scenarios.
        public static readonly TimeSpan TimeOut = TimeSpan.FromMinutes(2);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.Options.InitialSilosCount = siloCount;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .UseAzureBlobLeaseProvider(ob => ob.Configure<IOptions<ClusterOptions>>((options, cluster) =>
                    {
                        options.ConfigureTestDefaults();
                        options.BlobContainerName = "cluster-" + cluster.Value.ClusterId + "-leases";
                    }))
                    .UseAzureStorageClustering(options => options.ConfigureTestDefaults())
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b=>
                    {
                        b.ConfigurePartitioning(totalQueueCount);
                        b.UseLeaseBasedQueueBalancer(ob => ob.Configure(options =>
                        {
                            options.LeaseLength = TimeSpan.FromSeconds(15);
                            options.LeaseRenewPeriod = TimeSpan.FromSeconds(2);
                            options.LeaseAcquisitionPeriod = TimeSpan.FromSeconds(1);
                        }));
                    })
                    .ConfigureLogging(builder => builder.AddFilter($"LeaseBasedQueueBalancer-{StreamProviderName}", LogLevel.Trace))
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }

        [SkippableFact]
        public async Task LeaseBalancedQueueBalancer_SupportAutoScaleScenario()
        {
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(1, 2, mgmtGrain);
            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            await this.HostedCluster.StopSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(2, 2, mgmtGrain);
            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            await this.HostedCluster.StopSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(3, 3, mgmtGrain);
            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo(true);
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(2, 2, mgmtGrain);
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/9559")]
        public async Task LeaseBalancedQueueBalancer_SupportUnexpectedNodeFailureScenerio()
        {
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(1, 2, mgmtGrain);
            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            await this.HostedCluster.KillSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(2, 2, mgmtGrain);
            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            await this.HostedCluster.KillSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(3, 3, mgmtGrain);
            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo(true);
            await WaitUntilAgentManagersOwnCorrectAmountOfAgents(2, 2, mgmtGrain);
        }

        private static async Task WaitUntilAgentManagersOwnCorrectAmountOfAgents(int expectedAgentCountMin, int expectedAgentCountMax, IManagementGrain mgmtGrain)
        {
            int[] lastObservedCounts = null;
            Exception lastException = null;
            using var queueChanges = new StreamProviderQueueChangeSignal(StreamProviderName);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeOut)
            {
                var observedVersion = queueChanges.Version;
                try
                {
                    lastObservedCounts = await GetRunningAgentCounts(mgmtGrain);
                    lastException = null;
                    if (AgentManagersOwnCorrectAmountOfAgents(expectedAgentCountMin, expectedAgentCountMax, lastObservedCounts))
                    {
                        return;
                    }
                }
                catch (Exception exception)
                {
                    lastException = exception;
                }

                var remaining = TimeOut - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero || !await queueChanges.WaitForNextChangeAsync(observedVersion, remaining))
                {
                    break;
                }
            }

            try
            {
                lastObservedCounts = await GetRunningAgentCounts(mgmtGrain);
                lastException = null;
                if (AgentManagersOwnCorrectAmountOfAgents(expectedAgentCountMin, expectedAgentCountMax, lastObservedCounts))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                lastException = exception;
                throw new OrleansException(CreateUnexpectedAgentCountMessage(expectedAgentCountMin, expectedAgentCountMax, lastObservedCounts, exception), exception);
            }

            throw new OrleansException(CreateUnexpectedAgentCountMessage(expectedAgentCountMin, expectedAgentCountMax, lastObservedCounts, lastException));
        }

        private static async Task<int[]> GetRunningAgentCounts(IManagementGrain mgmtGrain)
        {
            object[] agentStarted = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents, null);
            return agentStarted.Select(startedAgentInEachSilo => Convert.ToInt32(startedAgentInEachSilo)).ToArray();
        }

        private static bool AgentManagersOwnCorrectAmountOfAgents(int expectedAgentCountMin, int expectedAgentCountMax, int[] counts)
        {
            return totalQueueCount == counts.Sum() &&
                counts.All(startedAgentInEachSilo => startedAgentInEachSilo <= expectedAgentCountMax && startedAgentInEachSilo >= expectedAgentCountMin);
        }

        private static string CreateUnexpectedAgentCountMessage(int expectedAgentCountMin, int expectedAgentCountMax, int[] counts, Exception exception = null)
        {
            var message =
                $"Agent managers don't own the expected amount of agents. Expected total: {totalQueueCount}; " +
                $"expected per manager range: [{expectedAgentCountMin}, {expectedAgentCountMax}]; " +
                $"observed counts: {FormatAgentCounts(counts)}.";
            return exception is null ? message : $"{message} Last query error: {exception.Message}";
        }

        private static string FormatAgentCounts(int[] counts) => counts is null ? "<none>" : $"[{string.Join(", ", counts)}] (sum: {counts.Sum()})";

        private sealed class StreamProviderQueueChangeSignal : IObserver<StreamingEvents.StreamingEvent>, IDisposable
        {
            private readonly string streamProviderName;
            private readonly IDisposable subscription;
            private readonly object lockObj = new();
            private TaskCompletionSource completion = CreateCompletion();
            private long version;

            public StreamProviderQueueChangeSignal(string streamProviderName)
            {
                this.streamProviderName = streamProviderName;
                this.subscription = StreamingEvents.AllEvents.Subscribe(this);
            }

            public long Version
            {
                get
                {
                    lock (lockObj)
                    {
                        return version;
                    }
                }
            }

            public async Task<bool> WaitForNextChangeAsync(long observedVersion, TimeSpan timeout)
            {
                Task task;
                lock (lockObj)
                {
                    if (version != observedVersion)
                    {
                        return true;
                    }

                    task = completion.Task;
                }

                try
                {
                    await task.WaitAsync(timeout);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            public void Dispose() => subscription.Dispose();

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(StreamingEvents.StreamingEvent value)
            {
                if (value is not StreamingEvents.BalancerChanged || value.StreamProvider != streamProviderName)
                {
                    return;
                }

                TaskCompletionSource toComplete;
                lock (lockObj)
                {
                    version++;
                    toComplete = completion;
                    completion = CreateCompletion();
                }

                toComplete.TrySetResult();
            }

            private static TaskCompletionSource CreateCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
