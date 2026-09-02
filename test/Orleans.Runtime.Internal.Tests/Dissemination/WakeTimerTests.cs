#nullable enable

using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public class WakeTimerTests
{
    [Fact]
    public async Task ChangeCompletesWaitWhenDueTimeElapses()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new WakeTimer(timeProvider);
        var wait = timer.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(wait.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(await wait);
    }

    [Fact]
    public async Task WakeBeforeWaitCompletesNextWait()
    {
        using var timer = new WakeTimer(TimeProvider.System);

        timer.Wake();

        Assert.True(await timer.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangeRearmsCurrentWait()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new WakeTimer(timeProvider);
        var wait = timer.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(wait.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(await wait);
    }

    [Fact]
    public async Task StaleCallbackDoesNotWakeRearmedTimer()
    {
        var timeProvider = new ControllableTimeProvider();
        using var timer = new WakeTimer(timeProvider);
        var wait = timer.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.FireTimer();

        Assert.False(wait.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        timeProvider.FireTimer();

        Assert.True(await wait);
    }

    [Fact]
    public async Task CancellingWaitDoesNotDisarmTimer()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new WakeTimer(timeProvider);
        using var cancellation = new CancellationTokenSource();
        timer.Change(TimeSpan.FromSeconds(1));
        var canceledWait = timer.WaitAsync(cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);

        var nextWait = timer.WaitAsync(TestContext.Current.CancellationToken).AsTask();
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await nextWait);
    }

    [Fact]
    public async Task WakeRacingCancelledWaitIsObservedByNextWaiter()
    {
        using var timer = new WakeTimer(TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var synchronizationContext = new QueuedSynchronizationContext();
        Task<bool> canceledWait;
        var previousSynchronizationContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            canceledWait = timer.WaitAsync(cancellation.Token).AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }

        cancellation.Cancel();
        timer.Wake();
        synchronizationContext.RunAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        var nextWait = timer.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(nextWait.IsCompletedSuccessfully);
        Assert.True(await nextWait);
    }

    [Fact]
    public async Task DisposeCompletesCurrentAndFutureWaitsWithFalse()
    {
        var timer = new WakeTimer(TimeProvider.System);
        var wait = timer.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        timer.Dispose();

        Assert.False(await wait);
        Assert.False(await timer.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeCompletesWaitAndDisposesTimerWithoutChangingIt()
    {
        var timeProvider = new ThrowingChangeTimeProvider();
        var timer = new WakeTimer(timeProvider);
        var wait = timer.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        timer.Dispose();

        Assert.False(await wait);
        Assert.True(timeProvider.Timer.IsDisposed);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _workItems = [];

        public override void Post(SendOrPostCallback callback, object? state) => _workItems.Enqueue((callback, state));

        public void RunAll()
        {
            while (_workItems.TryDequeue(out var workItem))
            {
                workItem.Callback(workItem.State);
            }
        }
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private long _timestamp;
        private ControllableTimer? _timer;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ControllableTimer(callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }

        public void FireTimer() => _timer!.Fire();

        private sealed class ControllableTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class ThrowingChangeTimeProvider : TimeProvider
    {
        public ThrowingChangeTimer Timer { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer;
    }

    private sealed class ThrowingChangeTimer : ITimer
    {
        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("Changing this timer is not supported.");

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
