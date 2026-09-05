using System.Collections.Concurrent;
using Orleans.Configuration;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CancellationTests;

/// <summary>
/// Tests for CancellationToken functionality with acknowledgement waiting enabled.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class CancellationTokenTests_WaitForAcknowledgement(CancellationTokenTests_WaitForAcknowledgement.Fixture fixture) : CancellationTokenTests(fixture), IClassFixture<CancellationTokenTests_WaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        // Wait for callees to acknowledge cancellation.
        public override bool WaitForCancellationAcknowledgement => true;
    }
}

/// <summary>
/// Tests for CancellationToken functionality with acknowledgement waiting disabled.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class CancellationTokenTests_NoWaitForAcknowledgement(CancellationTokenTests_NoWaitForAcknowledgement.Fixture fixture) : CancellationTokenTests(fixture), IClassFixture<CancellationTokenTests_NoWaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        // Do not wait for callees to acknowledge cancellation.
        public override bool WaitForCancellationAcknowledgement => false;
    }
}

/// <summary>
/// Base class for testing CancellationToken propagation and handling across grain calls.
/// </summary>
public abstract class CancellationTokenTests(CancellationTokenTests.FixtureBase fixture)
{
    public abstract class FixtureBase : BaseInProcessTestClusterFixture
    {
        public abstract bool WaitForCancellationAcknowledgement { get; }
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.Configure<SiloMessagingOptions>(options =>
                {
                    options.WaitForCancellationAcknowledgement = WaitForCancellationAcknowledgement;
                });
            });

            builder.ConfigureClient(clientBuilder =>
            {
                clientBuilder.Configure<ClientMessagingOptions>(options =>
                {
                    options.WaitForCancellationAcknowledgement = WaitForCancellationAcknowledgement;
                });
            });
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task GrainTaskCancellation(int delay)
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        var observer = new LongRunningTaskObserver();
        var observerReference = fixture.GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
        try
        {
            using var cts = new CancellationTokenSource();
            var callId = Guid.NewGuid();
            var grainTask = grain.LongWaitWithStartNotification(TimeSpan.FromSeconds(10), callId, observerReference, cts.Token);
            if (delay > 0)
            {
                // A timer does not guarantee that a new activation has begun executing the request.
                await observer.WaitForCallToStart(callId);
            }

            cts.CancelAfter(delay);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
            if (delay > 0)
            {
                await WaitForCallCancellation(grain, callId);
            }
        }
        finally
        {
            fixture.GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            GC.KeepAlive(observer);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task MultipleGrainsTaskCancellation(int delay)
    {
        using var cts = new CancellationTokenSource();
        var grains = Enumerable.Range(0, 5).Select(_ => fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid())).ToList();
        var callIds = grains.Select(_ => Guid.NewGuid()).ToArray();
        var observer = new LongRunningTaskObserver();
        var observerReference = fixture.GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
        try
        {
            var grainTasks = grains.Select((grain, index) =>
                delay > 0
                    ? grain.LongWaitInterleavingWithStartNotification(
                        TimeSpan.FromSeconds(10),
                        callIds[index],
                        observerReference,
                        cts.Token)
                    : grain.LongWaitInterleaving(
                        cts.Token,
                        TimeSpan.FromSeconds(10),
                        callIds[index]))
                .ToArray();
            if (delay > 0)
            {
                await Task.WhenAll(callIds.Select(observer.WaitForCallToStart));
            }

            cts.CancelAfter(delay);
            await Task.WhenAll(grainTasks.Select(task =>
                Assert.ThrowsAnyAsync<OperationCanceledException>(() => task)));
            if (delay > 0)
            {
                await Task.WhenAll(grains.Select((grain, index) =>
                    WaitForCallCancellation(grain, callIds[index])));
            }
        }
        finally
        {
            fixture.GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            GC.KeepAlive(observer);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task GrainTaskMultipleCancellations(int delay)
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        var callIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var cancellationSources = callIds.Select(_ => new CancellationTokenSource()).ToArray();
        var observer = new LongRunningTaskObserver();
        var observerReference = fixture.GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
        try
        {
            var grainTasks = callIds
                .Select((callId, index) => delay > 0
                    ? grain.LongWaitInterleavingWithStartNotification(
                        TimeSpan.FromSeconds(10),
                        callId,
                        observerReference,
                        cancellationSources[index].Token)
                    : grain.LongWaitInterleaving(
                        cancellationSources[index].Token,
                        TimeSpan.FromSeconds(10),
                        callId))
                .ToArray();
            if (delay > 0)
            {
                await Task.WhenAll(callIds.Select(observer.WaitForCallToStart));
            }

            foreach (var cancellationSource in cancellationSources)
            {
                cancellationSource.CancelAfter(delay);
            }

            await Task.WhenAll(grainTasks.Select(task =>
                Assert.ThrowsAnyAsync<OperationCanceledException>(() => task)));
            if (delay > 0)
            {
                await WaitForCallCancellation(grain, callIds);
            }
        }
        finally
        {
            foreach (var cancellationSource in cancellationSources)
            {
                cancellationSource.Dispose();
            }

            fixture.GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            GC.KeepAlive(observer);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        try
        {
            await grain.LongWait(cts.Token, TimeSpan.FromMilliseconds(1), Guid.Empty);
        }
        catch (Exception ex)
        {
            Assert.Fail("Expected no exception, but got: " + ex.Message);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task PreCancelledTokenPassing()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Except a OperationCanceledException to be thrown as the token is already cancelled
        Assert.Throws<OperationCanceledException>(() => grain.LongWait(cts.Token, TimeSpan.FromSeconds(10), Guid.Empty).Ignore());
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksExecutionContext()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.CancellationTokenCallbackResolve(cts.Token, callId);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        if (fixture.WaitForCancellationAcknowledgement)
        {
            var result = await grainTask;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        }

        await WaitForCallCancellation(grain, callId);
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksTaskSchedulerContext()
    {
        var grains = await GetGrains<bool>(false);

        var callId = Guid.NewGuid();
        var grainTask = grains.Item1.CallOtherCancellationTokenCallbackResolve(grains.Item2, callId);
        if (fixture.WaitForCancellationAcknowledgement)
        {
            var result = await grainTask;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        }

        await WaitForCallCancellation(grains.Item2, callId);
    }

    [Fact, TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksThrow_ExceptionDoesNotPropagate()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        grain.CancellationTokenCallbackThrow(cts.Token, callId).Ignore();
        // Cancellation is a cooperative mechanism, so we don't expect the exception to propagate
        cts.CancelAfter(100);
        await WaitForCallCancellation(grain, callId);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InSiloCancellation(int delay)
    {
        await CancellationTestCore(false, delay);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterSiloCancellation(int delay)
    {
        await CancellationTestCore(true, delay);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterSiloClientCancellationTokenPassing(int delay)
    {
        await ClientCancellationTokenPassing(delay, true);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InSiloClientCancellationTokenPassing(int delay)
    {
        await ClientCancellationTokenPassing(delay, false);
    }

    private async Task ClientCancellationTokenPassing(int delay, bool interSilo)
    {
        var grains = await GetGrains<bool>(interSilo);
        var grain = grains.Item1;
        var target = grains.Item2;
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        if (delay == 0)
        {
            var grainTask = grain.CallOtherLongRunningTask(target, cts.Token, TimeSpan.FromSeconds(10), callId);
            cts.CancelAfter(delay);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
            return;
        }

        var observer = new LongRunningTaskObserver();
        var observerReference = fixture.GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
        try
        {
            var grainTask = grain.CallOtherLongRunningTaskWithStartNotification(
                target,
                observerReference,
                cts.Token,
                TimeSpan.FromSeconds(10),
                callId);
            await observer.WaitForCallToStart(callId);

            cts.CancelAfter(delay);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
            await WaitForCallCancellation(target, callId);
        }
        finally
        {
            fixture.GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            GC.KeepAlive(observer);
        }
    }

    private async Task CancellationTestCore(bool interSilo, int delay)
    {
        var grains = await GetGrains<bool>(interSilo);
        var grain = grains.Item1;
        var target = grains.Item2;
        var callId = Guid.NewGuid();
        var grainTask = grain.CallOtherLongRunningTaskWithLocalCancellation(target, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(delay), callId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        if (delay > 0)
        {
            await WaitForCallCancellation(target, callId);
        }
    }

    private async Task<Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>> GetGrains<T1>(bool placeOnDifferentSilos = true)
    {
        var attemptNumber = 0;
        var attemptLimit = 50;
        ILongRunningTaskGrain<T1> grain, target;
        string instanceId, targetInstanceId;
        do
        {
            if (attemptNumber > 0)
            {
                if (attemptNumber >= attemptLimit)
                {
                    throw new Exception("Could not make requested grains placement");
                }

                await Task.Delay(500);
            }

            ++attemptNumber;
            var firstSilo = fixture.HostedCluster.Silos.First().SiloAddress;
            RequestContext.Set(IPlacementDirector.PlacementHintKey, firstSilo);
            grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            instanceId = await grain.GetRuntimeInstanceId();

            if (placeOnDifferentSilos)
            {
                var secondSilo = fixture.HostedCluster.Silos.Skip(1).First().SiloAddress;
                RequestContext.Set(IPlacementDirector.PlacementHintKey, secondSilo);
            }

            target = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            targetInstanceId = await target.GetRuntimeInstanceId();
            RequestContext.Clear();
        } while (placeOnDifferentSilos && instanceId.Equals(targetInstanceId, StringComparison.Ordinal) || !placeOnDifferentSilos && !instanceId.Equals(targetInstanceId, StringComparison.Ordinal));

        return new Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>(grain, target);
    }

    private async Task WaitForCallCancellation<T>(ILongRunningTaskGrain<T> grain, Guid callId)
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        await foreach (var (cancelledCallId, error) in grain.WatchCancellations(cts.Token))
        {
            if (cancelledCallId == callId)
            {
                if (error is not null)
                {
                    throw new Exception("Expected no error, but found an error", error);
                }

                return;
            }
        }

        Assert.Fail("Did not encounter the expected call id");
    }

    private async Task WaitForCallCancellation<T>(ILongRunningTaskGrain<T> grain, Guid[] callIds)
    {
        using var cts = new CancellationTokenSource();
        var targetIds = new HashSet<Guid>(callIds);
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        await foreach (var (cancelledCallId, error) in grain.WatchCancellations(cts.Token))
        {
            if (targetIds.Remove(cancelledCallId))
            {
                if (error is not null)
                {
                    throw new Exception("Expected no error, but found an error", error);
                }

                if (targetIds.Count == 0)
                {
                    return;
                }
            }
        }

        Assert.Fail("Did not encounter the expected call id");
    }

    private sealed class LongRunningTaskObserver : ILongRunningTaskObserver
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _startedCalls = new();

        public void OnCallStarted(Guid callId) => GetCallStarted(callId).TrySetResult();

        public Task WaitForCallToStart(Guid callId) => GetCallStarted(callId).Task.WaitAsync(TimeSpan.FromSeconds(30));

        private TaskCompletionSource GetCallStarted(Guid callId) =>
            _startedCalls.GetOrAdd(callId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>
    /// Tests that a running interleaving grain operation can be cancelled via CancellationToken.
    /// Interleaving requests run concurrently without queueing and should also be cancellable.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterleavingGrainTaskCancellation(int delay)
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        if (delay == 0)
        {
            var grainTask = grain.LongWaitInterleaving(cts.Token, TimeSpan.FromSeconds(10), callId);
            cts.CancelAfter(delay);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
            return;
        }

        var observer = new LongRunningTaskObserver();
        var observerReference = fixture.GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
        try
        {
            var grainTask = grain.LongWaitInterleavingWithStartNotification(
                TimeSpan.FromSeconds(10),
                callId,
                observerReference,
                cts.Token);
            await observer.WaitForCallToStart(callId);

            cts.CancelAfter(delay);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
            await WaitForCallCancellation(grain, callId);
        }
        finally
        {
            fixture.GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            GC.KeepAlive(observer);
        }
    }

    /// <summary>
    /// Tests that an interleaving request can be cancelled while a regular request is also running.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancelInterleavingWhileRegularGrainRequestRunning()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());

        // Start a regular (non-interleaving) long-running request
        using var regularCts = new CancellationTokenSource();
        var regularCallId = Guid.NewGuid();
        var regularTask = grain.LongWait(regularCts.Token, TimeSpan.FromSeconds(30), regularCallId);

        // Wait for the regular request to start
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Start an interleaving request (this should run concurrently)
        using var interleavingCts = new CancellationTokenSource();
        var interleavingCallId = Guid.NewGuid();
        var interleavingTask = grain.LongWaitInterleaving(interleavingCts.Token, TimeSpan.FromSeconds(10), interleavingCallId);

        // Wait a bit for the interleaving request to start
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Cancel the interleaving request
        await interleavingCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interleavingTask);
        await WaitForCallCancellation(grain, interleavingCallId);

        // Clean up - cancel the regular request
        await regularCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => regularTask);
    }

    /// <summary>
    /// Tests that multiple concurrent interleaving requests can each be cancelled independently.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task MultipleInterleavingGrainRequestsCancellation()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());

        // Start multiple interleaving requests concurrently
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        using var cts3 = new CancellationTokenSource();

        var callId1 = Guid.NewGuid();
        var callId2 = Guid.NewGuid();
        var callId3 = Guid.NewGuid();

        var task1 = grain.LongWaitInterleaving(cts1.Token, TimeSpan.FromSeconds(10), callId1);
        var task2 = grain.LongWaitInterleaving(cts2.Token, TimeSpan.FromSeconds(10), callId2);
        var task3 = grain.LongWaitInterleaving(cts3.Token, TimeSpan.FromSeconds(10), callId3);

        // Wait for all to be running
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Cancel only the second request
        await cts2.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);
        await WaitForCallCancellation(grain, callId2);

        // First and third should still be running, cancel them
        await cts1.CancelAsync();
        await cts3.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task3);

        await WaitForCallCancellation(grain, [callId1, callId3]);
    }
}
