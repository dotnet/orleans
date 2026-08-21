#nullable enable

using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.StreamingTests;

public abstract class StreamingResumeTests : TestClusterPerTest
{
    protected static readonly TimeSpan StreamInactivityPeriod = TimeSpan.FromSeconds(5);
    protected static readonly TimeSpan MetadataMinTimeInCache = StreamInactivityPeriod * 100;
    protected static readonly TimeSpan DataMaxAgeInCache = StreamInactivityPeriod * 5;
    protected static readonly TimeSpan DataMinTimeInCache = StreamInactivityPeriod * 4;

    protected const string StreamProviderName = "StreamingCacheMissTests";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    protected async Task WaitForStreamProviderReadyAsync()
    {
        await this.HostedCluster.WaitForLivenessToStabilizeAsync();

        var managementGrain = this.Client.GetGrain<IManagementGrain>(0);
        var activeSilos = this.HostedCluster.GetActiveSilos().ToArray();
        var expectedSiloCount = activeSilos.Length;
        var configuredQueueCounts = activeSilos
            .Select(silo => this.HostedCluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetOptionsByName<HashRingStreamQueueMapperOptions>(StreamProviderName)
                .TotalQueueCount)
            .Distinct()
            .ToArray();
        var expectedQueueCount = Assert.Single(configuredQueueCounts);
        await managementGrain.SendControlCommandToProvider<PersistentStreamProvider>(
            StreamProviderName,
            (int)PersistentStreamProviderCommand.StartAgents);

        // Guard against a subsequent membership notification racing the serialized StartAgents command.
        var consecutiveReadyObservations = 0;
        const int requiredReadyObservations = 3;
        await TestingUtils.WaitUntilAsync(
            async lastTry =>
            {
                var states = await managementGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                    StreamProviderName,
                    (int)PersistentStreamProviderCommand.GetAgentsState);
                var agentCounts = await managementGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                    StreamProviderName,
                    (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
                var runningAgentCounts = agentCounts.Select(Convert.ToInt32).ToArray();
                var ready = states.Length == expectedSiloCount
                    && runningAgentCounts.Length == expectedSiloCount
                    && states.All(state => Convert.ToInt32(state) == (int)StreamLifecycleOptions.RunState.AgentsStarted)
                    && runningAgentCounts.Sum() == expectedQueueCount;
                consecutiveReadyObservations = ready ? consecutiveReadyObservations + 1 : 0;

                if (lastTry)
                {
                    Assert.Equal(expectedSiloCount, states.Length);
                    Assert.Equal(expectedSiloCount, runningAgentCounts.Length);
                    Assert.All(states, state => Assert.Equal((int)StreamLifecycleOptions.RunState.AgentsStarted, Convert.ToInt32(state)));
                    Assert.Equal(expectedQueueCount, runningAgentCounts.Sum());
                    Assert.True(
                        consecutiveReadyObservations >= requiredReadyObservations,
                        $"Stream provider readiness was observed {consecutiveReadyObservations} consecutive time(s), but {requiredReadyObservations} were required.");
                }

                return consecutiveReadyObservations >= requiredReadyObservations;
            },
            WaitTimeout,
            delayOnFail: PollInterval);
    }

    [Fact]
    public virtual async Task ResumeAfterInactivity()
    {
        await ResumeAfterInactivityImpl(false);
    }

    [Fact]
    public virtual async Task ResumeAfterInactivityNotInCache()
    {
        await ResumeAfterInactivityImpl(true);
    }

    protected virtual async Task ResumeAfterInactivityImpl(bool waitForCacheToFlush)
    {
        using var observer = StreamingDiagnosticObserver.Create();
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
        var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);
        var interestingData = new byte[1] { 1 };

        await stream.OnNextAsync(interestingData);
        await WaitForItemDeliveryCountAsync(observer, streamId, 1);
        await WaitForEventCounterAsync(grain, 1);
        await WaitForStreamInactiveAsync(observer, streamId);

        if (waitForCacheToFlush)
        {
            var lastOtherKey = Guid.Empty;
            for (var i = 0; i < 5; i++)
            {
                lastOtherKey = Guid.NewGuid();
                var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), lastOtherKey);
                await otherStream.OnNextAsync(interestingData);
            }

            // Wait for the last other stream to go inactive, ensuring cache flush
            var lastOtherStreamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), lastOtherKey);
            await WaitForStreamInactiveAsync(observer, lastOtherStreamId);

            for (var i = 0; i < 5; i++)
            {
                var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid());
                await otherStream.OnNextAsync(interestingData);
            }
        }

        await stream.OnNextAsync(interestingData);
        await WaitForItemDeliveryCountAsync(observer, streamId, 2);
        await WaitForEventCounterAsync(grain, 2);

        Assert.Equal(0, await grain.GetErrorCounter());
    }

    [Fact]
    public virtual async Task ResumeAfterDeactivation()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
        var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);
        var interestingData = new byte[1] { 1 };

        await stream.OnNextAsync(interestingData);
        await WaitForItemDeliveryCountAsync(observer, streamId, 1);
        await WaitForEventCounterAsync(grain, 1);
        await WaitForStreamInactiveAsync(observer, streamId);
        await grain.Deactivate();

        await stream.OnNextAsync(interestingData);
        await WaitForItemDeliveryCountAsync(observer, streamId, 2);
        await WaitForEventCounterAsync(grain, 2);

        Assert.Equal(0, await grain.GetErrorCounter());
    }

    [Fact]
    public virtual async Task ResumeAfterDeactivationActiveStream()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid());
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
        await grain.DeactivateOnEvent(true);
        var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);
        var interestingData = new byte[1] { 1 };

        await stream.OnNextAsync(interestingData);
        await otherStream.OnNextAsync(interestingData);
        await otherStream.OnNextAsync(interestingData);
        await otherStream.OnNextAsync(interestingData);
        await stream.OnNextAsync(interestingData);
        await WaitForItemDeliveryCountAsync(observer, streamId, 2);
        await WaitForEventCounterAsync(grain, 2);
        await WaitForStreamInactiveAsync(observer, streamId);
        await grain.Deactivate();

        await stream.OnNextAsync(interestingData);
        await WaitForItemDeliveryCountAsync(observer, streamId, 3);
        await WaitForEventCounterAsync(grain, 3);

        Assert.Equal(0, await grain.GetErrorCounter());
    }

    [Fact]
    public virtual async Task ResumeAfterSlowSubscriber()
    {
        var key = Guid.NewGuid();
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var stream = streamProvider.GetStream<byte[]>("FastSlowImplicitSubscriptionCounterGrain", key);
        var fastGrain = this.Client.GetGrain<IFastImplicitSubscriptionCounterGrain>(key);
        var slowGrain = this.Client.GetGrain<ISlowImplicitSubscriptionCounterGrain>(key);

        await stream.OnNextAsync([1]);
        await TestingUtils.WaitUntilAsync((lastTry, cancellationToken) => CheckFastCounter(1, lastTry, cancellationToken), TimeSpan.FromSeconds(30), delayOnFail: PollInterval);

        await stream.OnNextAsync([2]);
        await TestingUtils.WaitUntilAsync((lastTry, cancellationToken) => CheckFastCounter(2, lastTry, cancellationToken), TimeSpan.FromSeconds(30), delayOnFail: PollInterval);

        async Task<bool> CheckFastCounter(int expected, bool lastTry, CancellationToken cancellationToken)
        {
            var actual = await fastGrain.GetEventCounter(cancellationToken);
            if (lastTry)
            {
                Assert.Equal(expected, actual);
            }

            return actual == expected;
        }
    }

    private static Task WaitForEventCounterAsync(IImplicitSubscriptionCounterGrain grain, int expected)
    {
        return TestingUtils.WaitUntilAsync(
            async (lastTry, cancellationToken) =>
            {
                var actual = await grain.GetEventCounter(cancellationToken);
                if (lastTry)
                {
                    Assert.Equal(expected, actual);
                }

                return actual == expected;
            },
            WaitTimeout,
            delayOnFail: PollInterval);
    }

    private static async Task WaitForItemDeliveryCountAsync(StreamingDiagnosticObserver observer, StreamId streamId, int expectedCount)
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await observer.WaitForItemDeliveryCountAsync(streamId, expectedCount, StreamProviderName, cts.Token);
    }

    private static async Task WaitForStreamInactiveAsync(StreamingDiagnosticObserver observer, StreamId streamId)
    {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await observer.WaitForStreamInactiveAsync(streamId, StreamProviderName, cts.Token);
    }
}
