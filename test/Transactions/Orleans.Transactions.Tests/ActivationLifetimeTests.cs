using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Transactions.State;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ActivationLifetimeTests
{
    [Fact]
    public async Task RepeatedLifecycleStops_WaitForAllAdmissionsAndKeepAdmissionClosed()
    {
        var lifecycle = new TestLifecycle();
        var lifetime = new ActivationLifetime(lifecycle, new ControlledTimeProvider());
        var cancellationCount = 0;
        using var registration = lifetime.OnDeactivating.Register(() =>
        {
            using var rejected = lifetime.TryBlockDeactivation();
            Assert.False(rejected.Entered);
            cancellationCount++;
        });

        Task lastStop;
        Task firstStop;
        using (var first = lifetime.TryBlockDeactivation())
        {
            Assert.True(first.Entered);
            using (var second = lifetime.TryBlockDeactivation())
            {
                Assert.True(second.Entered);
                lastStop = lifecycle.Observers[GrainLifecycleStage.Last].OnStop(TestContext.Current.CancellationToken);
                firstStop = lifecycle.Observers[GrainLifecycleStage.First].OnStop(TestContext.Current.CancellationToken);
                Assert.False(lastStop.IsCompleted);
                Assert.False(firstStop.IsCompleted);
            }

            Assert.False(lastStop.IsCompleted);
            Assert.False(firstStop.IsCompleted);
        }

        await Task.WhenAll(lastStop, firstStop).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, cancellationCount);
        Assert.True(lifetime.OnStop(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        await lifetime.OnStart(TestContext.Current.CancellationToken);
        using var late = lifetime.TryBlockDeactivation();
        Assert.False(late.Entered);

        var lateObserverCalled = false;
        using var lateRegistration = lifetime.OnDeactivating.Register(() => lateObserverCalled = true);
        Assert.True(lateObserverCalled);
    }

    [Fact]
    public void AlreadyCanceledHost_ClosesAdmissionAndSignalsObserversWithoutWaiting()
    {
        var timeProvider = new ControlledTimeProvider();
        var lifetime = new ActivationLifetime(new TestLifecycle(), timeProvider);
        using var admission = lifetime.TryBlockDeactivation();
        Assert.True(admission.Entered);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stop = lifetime.OnStop(cancellation.Token);

        Assert.True(stop.IsCompletedSuccessfully);
        Assert.True(lifetime.OnDeactivating.IsCancellationRequested);
        Assert.Empty(timeProvider.Timers);
        using var late = lifetime.TryBlockDeactivation();
        Assert.False(late.Entered);
    }

    [Fact]
    public async Task HostCancellation_CancelsOnlyTheWait()
    {
        var lifetime = CreateLifetime();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task repeatedStop;
        using (var admission = lifetime.TryBlockDeactivation())
        {
            Assert.True(admission.Entered);
            var stop = lifetime.OnStop(cancellation.Token);
            Assert.False(stop.IsCompleted);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            using var late = lifetime.TryBlockDeactivation();
            Assert.False(late.Entered);

            repeatedStop = lifetime.OnStop(TestContext.Current.CancellationToken);
            Assert.False(repeatedStop.IsCompleted);
        }

        await repeatedStop.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FiveSecondTimeout_EndsOnlyTheWaitAndPreservesAdmissionUntilDisposal()
    {
        var lifecycle = new TestLifecycle();
        var timeProvider = new ControlledTimeProvider();
        var lifetime = new ActivationLifetime(lifecycle, timeProvider);
        Task firstStop;
        using (var admission = lifetime.TryBlockDeactivation())
        {
            Assert.True(admission.Entered);
            var lastStop = lifecycle.Observers[GrainLifecycleStage.Last].OnStop(TestContext.Current.CancellationToken);
            Assert.True(lifetime.OnDeactivating.IsCancellationRequested);
            Assert.False(lastStop.IsCompleted);
            var timer = Assert.Single(timeProvider.Timers);
            Assert.Equal(TimeSpan.FromSeconds(5), timer.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);

            timer.Fire();
            await lastStop.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(timer.Disposed);
            using var late = lifetime.TryBlockDeactivation();
            Assert.False(late.Entered);

            firstStop = lifecycle.Observers[GrainLifecycleStage.First].OnStop(TestContext.Current.CancellationToken);
            Assert.False(firstStop.IsCompleted);
            Assert.Equal(2, timeProvider.Timers.Count);
            Assert.Equal(TimeSpan.FromSeconds(5), timeProvider.Timers[1].DueTime);
        }

        await firstStop.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancellationCallbackFailure_ReportsAllCallbacksAndKeepsAdmissionClosed()
    {
        var lifetime = CreateLifetime();
        var failure = new InvalidOperationException("observer failed");
        var otherObserverCalled = false;
        using var successfulRegistration = lifetime.OnDeactivating.Register(() => otherObserverCalled = true);
        using var failingRegistration = lifetime.OnDeactivating.Register(() => throw failure);
        Task repeatedStop;
        using (var admission = lifetime.TryBlockDeactivation())
        {
            Assert.True(admission.Entered);
            var exception = await Assert.ThrowsAsync<AggregateException>(() => lifetime.OnStop(TestContext.Current.CancellationToken));
            Assert.Same(failure, Assert.Single(exception.InnerExceptions));
            Assert.True(otherObserverCalled);
            using var late = lifetime.TryBlockDeactivation();
            Assert.False(late.Entered);
            repeatedStop = lifetime.OnStop(TestContext.Current.CancellationToken);
            Assert.False(repeatedStop.IsCompleted);
        }

        await repeatedStop.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void WorkException_ReleasesAdmission()
    {
        var lifetime = CreateLifetime();
        var failure = new InvalidOperationException("work failed");

        var exception = Assert.Throws<InvalidOperationException>(Work);

        Assert.Same(failure, exception);
        Assert.True(lifetime.OnStop(TestContext.Current.CancellationToken).IsCompletedSuccessfully);

        void Work()
        {
            using var admission = lifetime.TryBlockDeactivation();
            Assert.True(admission.Entered);
            throw failure;
        }
    }

    internal static ActivationLifetime CreateLifetime() => new(new TestLifecycle(), new ControlledTimeProvider());

    internal sealed class ClosedActivationLifetime : IActivationLifetime
    {
        private readonly AdmissionGate gate = new();

        public ClosedActivationLifetime() => _ = gate.CloseAsync();

        public CancellationToken OnDeactivating => CancellationToken.None;
        public int AdmissionAttempts { get; private set; }

        public AdmissionGate.Admission TryBlockDeactivation()
        {
            AdmissionAttempts++;
            return gate.TryEnter();
        }
    }

    private sealed class TestLifecycle : IGrainLifecycle
    {
        public Dictionary<int, ILifecycleObserver> Observers { get; } = new();

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Observers.Add(stage, observer);
            return new Subscription();
        }

        public void AddMigrationParticipant(IGrainMigrationParticipant participant) => throw new NotSupportedException();

        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) => throw new NotSupportedException();

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        public List<ControlledTimer> Timers { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state, dueTime, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public bool Disposed { get; private set; }

        public void Fire()
        {
            Assert.False(Disposed);
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
