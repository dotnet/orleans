using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Xunit;
using static Orleans.InvokableObjectManager;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestCategory("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class AsyncEnumerableGrainObserverExtensionTests
{
    private static AsyncEnumerableGrainObserverExtension CreateExtension()
    {
        var context = Substitute.For<IGrainContext>();
        var timeProvider = TimeProvider.System;
        var messagingOptions = Options.Create(new SiloMessagingOptions { ResponseTimeout = TimeSpan.FromSeconds(30) });
        var logger = NullLogger<AsyncEnumerableGrainObserverExtension>.Instance;

        return new AsyncEnumerableGrainObserverExtension(context, timeProvider, messagingOptions, logger);
    }

    private static TestAsyncEnumerableRequest<int> CreateRequest(params int[] values) =>
        new(() => IterateAsync(values));

    private static TestAsyncEnumerableRequest<int> CreateGateableRequest(
        TaskCompletionSource started, TaskCompletionSource finallyStarted, TaskCompletionSource release) =>
        new(() => GatedIterateAsync(started, finallyStarted, release), maxBatchSize: 2);

    private static async IAsyncEnumerable<int> IterateAsync(IEnumerable<int> values)
    {
        foreach (var val in values)
        {
            yield return val;
            await Task.Yield();
        }
    }

    // Iterates one value then parks on cancellation. The producer observes the [EnumeratorCancellation] token
    // so teardown can reach the finally block by cancelling ClientTokenSource. finallyStarted signals that the
    // producer is inside finally (i.e. teardown is blocked waiting for it), and release unblocks teardown.
    private static async IAsyncEnumerable<int> GatedIterateAsync(
        TaskCompletionSource started,
        TaskCompletionSource finallyStarted,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            yield return 1;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            finallyStarted.TrySetResult();
            await release.Task;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartEnumeration_InvalidBatchSize_Throws(int maxBatchSize)
    {
        await using var extension = CreateExtension();
        var request = CreateRequest(1, 2);
        request.MaxBatchSize = maxBatchSize;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await extension.StartEnumeration(Guid.NewGuid(), request, CancellationToken.None));
    }

    [Fact]
    public async Task StartEnumeration_DuplicateId_Throws()
    {
        await using var extension = CreateExtension();
        var requestId = Guid.NewGuid();

        await extension.StartEnumeration(requestId, CreateRequest(1), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await extension.StartEnumeration(requestId, CreateRequest(2), CancellationToken.None));
    }

    [Fact]
    public async Task MoveNext_UnknownId_ReturnsMissingEnumerator()
    {
        await using var extension = CreateExtension();
        var result = await extension.MoveNext<int>(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(EnumerationResult.MissingEnumeratorError, result.Status);
    }

    [Fact]
    public async Task MoveNext_WrongType_ThrowsAndKeepsEnumerator()
    {
        await using var extension = CreateExtension();
        var requestId = Guid.NewGuid();

        // We start as int, but then pull as string.
        await extension.StartEnumeration(requestId, CreateRequest(1, 2), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidCastException>(async () =>
            await extension.MoveNext<string>(requestId, CancellationToken.None));

        // A pull with the right element type continues the same enumeration.
        var result = await extension.MoveNext<int>(requestId, CancellationToken.None);
        Assert.Equal(EnumerationResult.Element, result.Status);
        Assert.Equal(2, result.Value);
    }

    [Fact]
    public async Task StartEnumeration_AfterDispose_Throws()
    {
        await using var extension = CreateExtension();

        await extension.DisposeAsync();

        await Assert.ThrowsAsync<ObserverNotReachableException>(async () =>
            await extension.StartEnumeration(Guid.NewGuid(), CreateRequest(1), CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_JoinsProducerTeardown()
    {
        await using var extension = CreateExtension();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finallyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Guid.NewGuid();

        // First pull returns element 1 and parks the producer in Task.Delay(Infinite, linkedToken).
        await extension.StartEnumeration(requestId, CreateGateableRequest(started, finallyStarted, release), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var disposeTask = extension.DisposeAsync(requestId, CancellationToken.None).AsTask();

        try
        {
            // Wait deterministically for the producer to enter its finally block. At that instant teardown
            // is blocked in GracefullyWaitForCompletionAsync on the still-pending producer MoveNextAsync,
            // so disposeTask cannot possibly be complete.
            await finallyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.False(disposeTask.IsCompleted, "DisposeAsync must not complete before producer teardown finishes.");
        }
        finally
        {
            release.TrySetResult();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task DisposeAsync_RepliesBeforeCallerTimeoutWhenTeardownIsBlocked()
    {
        var timeProvider = new FakeTimeProvider();
        var responseTimeout = TimeSpan.FromSeconds(30);
        await using var extension = new AsyncEnumerableGrainObserverExtension(
            Substitute.For<IGrainContext>(),
            timeProvider,
            Options.Create(new SiloMessagingOptions { ResponseTimeout = responseTimeout }),
            NullLogger<AsyncEnumerableGrainObserverExtension>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finallyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Guid.NewGuid();

        try
        {
            await extension.StartEnumeration(requestId, CreateGateableRequest(started, finallyStarted, release), CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            var disposeTask = extension.DisposeAsync(requestId, CancellationToken.None).AsTask();
            await finallyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.False(disposeTask.IsCompleted);

            // The producer's teardown stays blocked, but the reply must not wait for the caller's full response timeout.
            timeProvider.Advance(responseTimeout / 2);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task CleanupLoop_ReusesTimerAcrossConsecutiveEnumerations()
    {
        var timeProvider = new TrackingTimeProvider();
        await using var extension = CreateExtension(timeProvider);
        var firstId = Guid.NewGuid();

        await extension.StartEnumeration(firstId, CreateRequest(1, 2), CancellationToken.None);
        await extension.DisposeAsync(firstId, CancellationToken.None);
        await extension.StartEnumeration(Guid.NewGuid(), CreateRequest(3, 4), CancellationToken.None);

        Assert.Equal(1, timeProvider.PeriodicTimerCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterIdleCleanupLoopStops_Completes()
    {
        var timeProvider = new TrackingTimeProvider();
        var extension = CreateExtension(timeProvider);
        var requestId = Guid.NewGuid();

        await extension.StartEnumeration(requestId, CreateRequest(1, 2), CancellationToken.None);
        await extension.DisposeAsync(requestId, CancellationToken.None);
        timeProvider.Clock.Advance(TimeSpan.FromSeconds(30));
        await timeProvider.TimerDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await extension.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static AsyncEnumerableGrainObserverExtension CreateExtension(TimeProvider timeProvider) =>
        new(
            Substitute.For<IGrainContext>(),
            timeProvider,
            Options.Create(new SiloMessagingOptions { ResponseTimeout = TimeSpan.FromSeconds(30) }),
            NullLogger<AsyncEnumerableGrainObserverExtension>.Instance);

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private int _periodicTimerCount;
        public FakeTimeProvider Clock { get; } = new();
        public TaskCompletionSource TimerDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PeriodicTimerCount => Volatile.Read(ref _periodicTimerCount);
        public override DateTimeOffset GetUtcNow() => Clock.GetUtcNow();
        public override long GetTimestamp() => Clock.GetTimestamp();
        public override long TimestampFrequency => Clock.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Clock.CreateTimer(callback, state, dueTime, period);
            if (period != Timeout.InfiniteTimeSpan)
            {
                Interlocked.Increment(ref _periodicTimerCount);
                return new TrackingTimer(timer, TimerDisposed);
            }

            return timer;
        }

        private sealed class TrackingTimer(ITimer inner, TaskCompletionSource disposed) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                inner.Dispose();
                disposed.TrySetResult();
            }

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                disposed.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task MoveNext_ClientTokenCanceledWhileParked_ReturnsCanceled()
    {
        await using var extension = CreateExtension();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finallyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        try
        {
            // First pull returns element 1 and parks the producer, so state.MoveNextTask is pending.
            var startResult = await extension.StartEnumeration(requestId, CreateGateableRequest(started, finallyStarted, release), CancellationToken.None);
            Assert.Equal(EnumerationResult.Element, startResult.Status);
            Assert.Equal(1, startResult.Value);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            // Second pull with a cancellable token observes the pending producer task and parks in WaitAsync.
            var moveNextTask = extension.MoveNext<int>(requestId, cts.Token).AsTask();
            cts.Cancel();

            var result = await moveNextTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(EnumerationResult.Canceled, result.Status);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
