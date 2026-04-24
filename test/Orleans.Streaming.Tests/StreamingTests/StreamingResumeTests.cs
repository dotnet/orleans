#nullable enable

using Orleans.Streams;
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

    [SkippableFact]
    public virtual async Task ResumeAfterInactivity()
    {
        await ResumeAfterInactivityImpl(false);
    }

    [SkippableFact]
    public virtual async Task ResumeAfterInactivityNotInCache()
    {
        await ResumeAfterInactivityImpl(true);
    }

    protected virtual async Task ResumeAfterInactivityImpl(bool waitForCacheToFlush)
    {
        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
        var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);
        var interestingData = new byte[1] { 1 };

        await stream.OnNextAsync(interestingData);
        await observer.WaitForItemDeliveryCountAsync(streamId, 1, StreamProviderName, cts.Token);
        await WaitForEventCounterAsync(grain, 1);
        await observer.WaitForStreamInactiveAsync(streamId, StreamProviderName, cts.Token);

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
            await observer.WaitForStreamInactiveAsync(lastOtherStreamId, StreamProviderName, cts.Token);

            for (var i = 0; i < 5; i++)
            {
                var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid());
                await otherStream.OnNextAsync(interestingData);
            }
        }

        await stream.OnNextAsync(interestingData);
        await observer.WaitForItemDeliveryCountAsync(streamId, 2, StreamProviderName, cts.Token);
        await WaitForEventCounterAsync(grain, 2);

        Assert.Equal(0, await grain.GetErrorCounter());
    }

    [SkippableFact]
    public virtual async Task ResumeAfterDeactivation()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
        var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);
        var interestingData = new byte[1] { 1 };

        await stream.OnNextAsync(interestingData);
        await observer.WaitForItemDeliveryCountAsync(streamId, 1, StreamProviderName, cts.Token);
        await WaitForEventCounterAsync(grain, 1);
        await observer.WaitForStreamInactiveAsync(streamId, StreamProviderName, cts.Token);
        await grain.Deactivate();

        await stream.OnNextAsync(interestingData);
        await observer.WaitForItemDeliveryCountAsync(streamId, 2, StreamProviderName, cts.Token);
        await WaitForEventCounterAsync(grain, 2);

        Assert.Equal(0, await grain.GetErrorCounter());
    }

    [SkippableFact]
    public virtual async Task ResumeAfterDeactivationActiveStream()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
        await observer.WaitForItemDeliveryCountAsync(streamId, 2, StreamProviderName, cts.Token);
        await WaitForEventCounterAsync(grain, 2);
        await observer.WaitForStreamInactiveAsync(streamId, StreamProviderName, cts.Token);
        await grain.Deactivate();

        await stream.OnNextAsync(interestingData);
        await observer.WaitForItemDeliveryCountAsync(streamId, 3, StreamProviderName, cts.Token);
        await WaitForEventCounterAsync(grain, 3);

        Assert.Equal(0, await grain.GetErrorCounter());
    }

    [SkippableFact]
    public virtual async Task ResumeAfterSlowSubscriber()
    {
        var key = Guid.NewGuid();
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
        var stream = streamProvider.GetStream<byte[]>("FastSlowImplicitSubscriptionCounterGrain", key);
        var fastGrain = this.Client.GetGrain<IFastImplicitSubscriptionCounterGrain>(key);
        var slowGrain = this.Client.GetGrain<ISlowImplicitSubscriptionCounterGrain>(key);

        await stream.OnNextAsync([1]);
        await TestingUtils.WaitUntilAsync(lastTry => CheckFastCounter(1, lastTry), TimeSpan.FromSeconds(30), delayOnFail: PollInterval);

        await stream.OnNextAsync([2]);
        await TestingUtils.WaitUntilAsync(lastTry => CheckFastCounter(2, lastTry), TimeSpan.FromSeconds(30), delayOnFail: PollInterval);

        async Task<bool> CheckFastCounter(int expected, bool lastTry)
        {
            var actual = await fastGrain.GetEventCounter();
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
            async lastTry =>
            {
                var actual = await grain.GetEventCounter();
                if (lastTry)
                {
                    Assert.Equal(expected, actual);
                }

                return actual == expected;
            },
            TimeSpan.FromSeconds(30),
            delayOnFail: PollInterval);
    }
}
