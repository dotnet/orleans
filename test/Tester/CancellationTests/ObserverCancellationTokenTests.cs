#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CancellationTests;

/// <summary>
/// Tests for Observer CancellationToken functionality with acknowledgement waiting enabled.
/// </summary>
public sealed class ObserverCancellationTokenTests_WaitForAcknowledgement(ObserverCancellationTokenTests_WaitForAcknowledgement.Fixture fixture)
    : ObserverCancellationTokenTests(fixture), IClassFixture<ObserverCancellationTokenTests_WaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        public override bool WaitForCancellationAcknowledgement => true;
    }
}

/// <summary>
/// Tests for Observer CancellationToken functionality with acknowledgement waiting disabled.
/// </summary>
public sealed class ObserverCancellationTokenTests_NoWaitForAcknowledgement(ObserverCancellationTokenTests_NoWaitForAcknowledgement.Fixture fixture)
    : ObserverCancellationTokenTests(fixture), IClassFixture<ObserverCancellationTokenTests_NoWaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        public override bool WaitForCancellationAcknowledgement => false;
    }
}

/// <summary>
/// Base class for testing CancellationToken propagation and handling for observers (local objects).
/// </summary>
public abstract class ObserverCancellationTokenTests(ObserverCancellationTokenTests.FixtureBase fixture)
{
    public abstract class FixtureBase : BaseInProcessTestClusterFixture
    {
        public abstract bool WaitForCancellationAcknowledgement { get; }

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.ConfigureHost(hostBuilder => hostBuilder.Logging.SetMinimumLevel(LogLevel.Debug));
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

    /// <summary>
    /// Tests that a running observer operation can be cancelled via CancellationToken.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObserverTaskCancellation(bool cancelImmediately)
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.NotifyLongWait(TimeSpan.FromSeconds(10), callId, cts.Token);

        if (cancelImmediately)
        {
            await cts.CancelAsync();
        }
        else
        {
            await observer.WaitForCallToStart(callId);
            await cts.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        if (!cancelImmediately)
        {
            await observer.WaitForCancellation(callId);
        }

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests that a pre-cancelled token is properly handled when calling an observer.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task PreCancelledTokenPassing()
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Expect an OperationCanceledException to be thrown as the token is already cancelled
        Assert.Throws<OperationCanceledException>(() =>
            grain.NotifyLongWait(TimeSpan.FromSeconds(10), Guid.Empty, cts.Token).Ignore());

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests that passing a CancellationToken without cancellation does not throw.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        using var cts = new CancellationTokenSource();
        try
        {
            await grain.NotifyLongWait(TimeSpan.FromMilliseconds(1), Guid.Empty, cts.Token);
        }
        catch (Exception ex)
        {
            Assert.Fail("Expected no exception, but got: " + ex.Message);
        }

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests that cancellation token callbacks execute in the correct execution context for observers.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksExecutionContext()
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.NotifyCancellationTokenCallbackResolve(callId, cts.Token);

        await observer.WaitForCallToStart(callId);
        await cts.CancelAsync();

        if (fixture.WaitForCancellationAcknowledgement)
        {
            var result = await grainTask;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        }

        await observer.WaitForCancellation(callId);
        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests cancellation when multiple observers are subscribed and all receive cancellation.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleObserversCancellation(bool cancelImmediately)
    {
        // Create multiple grains each with an observer
        using var cts = new CancellationTokenSource();
        var grains = new List<(IObserverWithCancellationGrain Grain, LongRunningObserver Observer, ILongRunningObserver Reference, Guid CallId)>();

        for (int i = 0; i < 5; i++)
        {
            var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
            var observer = new LongRunningObserver();
            var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
            await grain.Subscribe(reference);
            grains.Add((grain, observer, reference, Guid.NewGuid()));
        }

        var notifyTasks = grains
            .Select(g => g.Grain.NotifyLongWait(TimeSpan.FromSeconds(10), g.CallId, cts.Token))
            .ToList();

        if (cancelImmediately)
        {
            await cts.CancelAsync();
        }
        else
        {
            await Task.WhenAll(grains.Select(g => g.Observer.WaitForCallToStart(g.CallId)));
            await cts.CancelAsync();
        }

        foreach (var task in notifyTasks)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        if (!cancelImmediately)
        {
            for (int i = 0; i < grains.Count; i++)
            {
                await grains[i].Observer.WaitForCancellation(grains[i].CallId);
            }
        }

        foreach (var g in grains)
        {
            await g.Grain.Unsubscribe(g.Reference);
        }
        foreach (var g in grains)
        {
            fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(g.Reference);
        }
    }

    /// <summary>
    /// Tests that cancellation of a waiting (queued) request in an observer is handled correctly.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancelWaitingRequest()
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        // Start a long-running request that will block the observer
        using var blockingCts = new CancellationTokenSource();
        var blockingCallId = Guid.NewGuid();
        var blockingTask = grain.NotifyLongWait(TimeSpan.FromSeconds(30), blockingCallId, blockingCts.Token);

        // Wait for the blocking request to start
        await observer.WaitForCallToStart(blockingCallId);

        // Start another request that will be queued
        using var queuedCts = new CancellationTokenSource();
        var queuedCallId = Guid.NewGuid();
        var queuedTask = grain.NotifyLongWait(TimeSpan.FromSeconds(10), queuedCallId, queuedCts.Token);

        // Cancel the queued request
        await queuedCts.CancelAsync();

        // The queued task should throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);

        // Cancel the blocking request to clean up
        await blockingCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockingTask);
        await observer.WaitForCancellation(blockingCallId);

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests that a running interleaving observer operation can be cancelled via CancellationToken.
    /// Interleaving requests run concurrently without queueing and should also be cancellable.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterleavingObserverTaskCancellation(bool cancelImmediately)
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.NotifyInterleavingLongWait(TimeSpan.FromSeconds(10), callId, cts.Token);

        if (cancelImmediately)
        {
            await cts.CancelAsync();
        }
        else
        {
            await observer.WaitForCallToStart(callId);
            await cts.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        if (!cancelImmediately)
        {
            await observer.WaitForCancellation(callId);
        }

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests that multiple concurrent interleaving requests can each be cancelled independently.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task MultipleInterleavingRequestsCancellation()
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        // Start multiple interleaving requests concurrently
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        using var cts3 = new CancellationTokenSource();

        var callId1 = Guid.NewGuid();
        var callId2 = Guid.NewGuid();
        var callId3 = Guid.NewGuid();

        var task1 = grain.NotifyInterleavingLongWait(TimeSpan.FromSeconds(10), callId1, cts1.Token);
        var task2 = grain.NotifyInterleavingLongWait(TimeSpan.FromSeconds(10), callId2, cts2.Token);
        var task3 = grain.NotifyInterleavingLongWait(TimeSpan.FromSeconds(10), callId3, cts3.Token);

        // Wait for all to be running
        await Task.WhenAll(
            observer.WaitForCallToStart(callId1),
            observer.WaitForCallToStart(callId2),
            observer.WaitForCallToStart(callId3));

        // Cancel only the second request
        await cts2.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);
        await observer.WaitForCancellation(callId2);

        // First and third should still be running, cancel them
        await cts1.CancelAsync();
        await cts3.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task3);

        await observer.WaitForCancellation(callId1);
        await observer.WaitForCancellation(callId3);

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Tests that an interleaving request can be cancelled while a regular request is also running.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancelInterleavingWhileRegularRequestRunning()
    {
        var grain = fixture.GrainFactory.GetGrain<IObserverWithCancellationGrain>(Guid.NewGuid());
        var observer = new LongRunningObserver();
        var reference = fixture.GrainFactory.CreateObjectReference<ILongRunningObserver>(observer);
        await grain.Subscribe(reference);

        // Start a regular (non-interleaving) long-running request
        using var regularCts = new CancellationTokenSource();
        var regularCallId = Guid.NewGuid();
        var regularTask = grain.NotifyLongWait(TimeSpan.FromSeconds(30), regularCallId, regularCts.Token);

        // Wait for the regular request to start
        await observer.WaitForCallToStart(regularCallId);

        // Start an interleaving request (this should run concurrently)
        using var interleavingCts = new CancellationTokenSource();
        var interleavingCallId = Guid.NewGuid();
        var interleavingTask = grain.NotifyInterleavingLongWait(TimeSpan.FromSeconds(10), interleavingCallId, interleavingCts.Token);

        // Wait for the interleaving request to start
        await observer.WaitForCallToStart(interleavingCallId);

        // Cancel the interleaving request
        await interleavingCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interleavingTask);
        await observer.WaitForCancellation(interleavingCallId);

        // Clean up - cancel the regular request
        await regularCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => regularTask);
        await observer.WaitForCancellation(regularCallId);

        await grain.Unsubscribe(reference);
        fixture.GrainFactory.DeleteObjectReference<ILongRunningObserver>(reference);
    }

    /// <summary>
    /// Client-side observer implementation for testing long-running operations with cancellation.
    /// </summary>
    private sealed class LongRunningObserver : ILongRunningObserver
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _callStartedTcs = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _callCancelledTcs = new();

        /// <inheritdoc />
        public async Task LongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken)
        {
            var startedTcs = _callStartedTcs.GetOrAdd(callId, _ => new TaskCompletionSource());
            var cancelledTcs = _callCancelledTcs.GetOrAdd(callId, _ => new TaskCompletionSource());

            startedTcs.TrySetResult();

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelledTcs.TrySetResult();
                throw;
            }
        }

        /// <inheritdoc />
        public Task<bool> CancellationTokenCallbackResolve(Guid callId, CancellationToken cancellationToken)
        {
            var startedTcs = _callStartedTcs.GetOrAdd(callId, _ => new TaskCompletionSource());
            var cancelledTcs = _callCancelledTcs.GetOrAdd(callId, _ => new TaskCompletionSource());
            var resultTcs = new TaskCompletionSource<bool>();

            startedTcs.TrySetResult();

            cancellationToken.Register(() =>
            {
                cancelledTcs.TrySetResult();
                resultTcs.TrySetResult(true);
            });

            return resultTcs.Task;
        }

        /// <inheritdoc />
        public async Task InterleavingLongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken)
        {
            var startedTcs = _callStartedTcs.GetOrAdd(callId, _ => new TaskCompletionSource());
            var cancelledTcs = _callCancelledTcs.GetOrAdd(callId, _ => new TaskCompletionSource());

            startedTcs.TrySetResult();

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelledTcs.TrySetResult();
                throw;
            }
        }

        /// <summary>
        /// Waits for a call to start.
        /// </summary>
        public Task WaitForCallToStart(Guid callId)
        {
            var tcs = _callStartedTcs.GetOrAdd(callId, _ => new TaskCompletionSource());
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// Waits for a call to be cancelled.
        /// </summary>
        public Task WaitForCancellation(Guid callId)
        {
            var tcs = _callCancelledTcs.GetOrAdd(callId, _ => new TaskCompletionSource());
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
