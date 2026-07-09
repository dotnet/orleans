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
        var wait = timer.WaitAsync(CancellationToken.None).AsTask();

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

        Assert.True(await timer.WaitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ChangeRearmsCurrentWait()
    {
        var timeProvider = new FakeTimeProvider();
        using var timer = new WakeTimer(timeProvider);
        var wait = timer.WaitAsync(CancellationToken.None).AsTask();

        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        timer.Change(TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(wait.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

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

        var nextWait = timer.WaitAsync(CancellationToken.None).AsTask();
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
        var nextWait = timer.WaitAsync();
        Assert.True(nextWait.IsCompletedSuccessfully);
        Assert.True(await nextWait);
    }

    [Fact]
    public async Task DisposeCompletesCurrentAndFutureWaitsWithFalse()
    {
        var timer = new WakeTimer(TimeProvider.System);
        var wait = timer.WaitAsync(CancellationToken.None).AsTask();

        timer.Dispose();

        Assert.False(await wait);
        Assert.False(await timer.WaitAsync(CancellationToken.None));
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
}
