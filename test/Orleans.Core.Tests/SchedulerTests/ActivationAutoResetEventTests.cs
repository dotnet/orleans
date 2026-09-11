#nullable enable

using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using UnitTests.TesterInternal;
using Xunit;

#pragma warning disable xUnit1031 // These tests manually consume completed ValueTaskSource awaiters to verify reset and token behavior.

namespace UnitTests.SchedulerTests;

[TestCategory("BVT"), TestCategory("Scheduler")]
public class ActivationAutoResetEventTests
{
    [Fact]
    public async Task SignalBeforeWaitCompletesSynchronously()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        signal.Signal();
        var wait = signal.WaitAsync();

        Assert.True(wait.IsCompletedSuccessfully);
        await wait;
    }

    [Fact]
    public async Task MultipleSignalsBeforeWaitCoalesceIntoOneCompletion()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        signal.Signal();
        signal.Signal();

        await signal.WaitAsync();
        var nextWait = signal.WaitAsync();
        Assert.False(nextWait.IsCompleted);

        signal.Signal();
        await nextWait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitBeforeSignalCompletesAfterSignal()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        var wait = signal.WaitAsync();
        Assert.False(wait.IsCompleted);

        signal.Signal();

        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SignalCanBeReusedAcrossRepeatedCycles()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        signal.Signal();
        await signal.WaitAsync();

        var wait = signal.WaitAsync();
        Assert.False(wait.IsCompleted);

        signal.Signal();
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        signal.Signal();
        await signal.WaitAsync();
    }

    [Fact]
    public async Task SignalWhileCompletionIsPendingCoalescesAndSignalAfterResetCompletesNextWait()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var wait = signal.WaitAsync();
        var awaiter = wait.GetAwaiter();
        var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        awaiter.UnsafeOnCompleted(continuationRan.SetResult);
        signal.Signal();
        await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

        signal.Signal();
        awaiter.GetResult();

        var nextWait = signal.WaitAsync();
        Assert.False(nextWait.IsCompleted);

        signal.Signal();
        await nextWait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentSignalersReleaseOneWaiterAndCoalesce()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var wait = signal.WaitAsync();
        var awaiter = wait.GetAwaiter();
        var continuationCount = 0;
        var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new CountdownEvent(8);
        using var start = new ManualResetEventSlim();
        var signalers = new Task[ready.InitialCount];

        awaiter.UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref continuationCount);
            continuationRan.TrySetResult();
        });

        for (var i = 0; i < signalers.Length; i++)
        {
            signalers[i] = Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                signal.Signal();
            });
        }

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        await Task.WhenAll(signalers).WaitAsync(TimeSpan.FromSeconds(5));
        await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, continuationCount);
        awaiter.GetResult();
        var nextWait = signal.WaitAsync();
        Assert.False(nextWait.IsCompleted);

        signal.Signal();
        await nextWait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentWaitersAreRejected()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        var wait = signal.WaitAsync();
        Assert.False(wait.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => signal.WaitAsync());

        signal.Signal();
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationRegistrationBeforeOrAfterSignalRunsExactlyOnce(bool signalFirst)
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var wait = signal.WaitAsync();
        var awaiter = wait.GetAwaiter();
        var continuationCount = 0;
        var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (signalFirst)
        {
            signal.Signal();
        }

        awaiter.UnsafeOnCompleted(() =>
        {
            Interlocked.Increment(ref continuationCount);
            continuationRan.TrySetResult();
        });

        if (!signalFirst)
        {
            signal.Signal();
        }

        await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        awaiter.GetResult();
        Assert.Equal(1, continuationCount);
    }

    [Fact]
    public async Task SecondContinuationRegistrationIsRejectedWithoutReplacingFirst()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var source = (IValueTaskSource)signal;
        var wait = signal.WaitAsync();
        var firstContinuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondContinuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        source.OnCompleted(
            static state => ((TaskCompletionSource)state!).SetResult(),
            firstContinuationRan,
            token: 0,
            ValueTaskSourceOnCompletedFlags.None);
        Assert.Throws<InvalidOperationException>(() => source.OnCompleted(
            static state => ((TaskCompletionSource)state!).SetResult(),
            secondContinuationRan,
            token: 0,
            ValueTaskSourceOnCompletedFlags.None));

        signal.Signal();
        await firstContinuationRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondContinuationRan.Task.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task ConcurrentContinuationRegistrationsPreserveWinningState()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);

        for (var i = 0; i < 100; i++)
        {
            var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
            var source = (IValueTaskSource)signal;
            var wait = signal.WaitAsync();
            var firstContinuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondContinuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var start = new Barrier(3);

            var firstRegistered = Register(firstContinuationRan);
            var secondRegistered = Register(secondContinuationRan);

            start.SignalAndWait();
            var registrations = await Task.WhenAll(firstRegistered, secondRegistered).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, registrations.Count(static registered => registered));

            signal.Signal();
            var winningContinuation = registrations[0] ? firstContinuationRan : secondContinuationRan;
            var losingContinuation = registrations[0] ? secondContinuationRan : firstContinuationRan;
            await winningContinuation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(losingContinuation.Task.IsCompleted);
            await wait;

            Task<bool> Register(TaskCompletionSource completion)
            {
                return Task.Run(() =>
                {
                    start.SignalAndWait();
                    try
                    {
                        source.OnCompleted(
                            static state => ((TaskCompletionSource)state!).SetResult(),
                            completion,
                            token: 0,
                            ValueTaskSourceOnCompletedFlags.None);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                });
            }
        }
    }

    [Fact]
    public async Task ContinuationRegistrationAndSignalRaceCompletesExactlyOnce()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        for (var i = 0; i < 100; i++)
        {
            var wait = signal.WaitAsync();
            var awaiter = wait.GetAwaiter();
            var continuationCount = 0;
            var continuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var start = new Barrier(3);

            var register = Task.Run(() =>
            {
                start.SignalAndWait();
                awaiter.UnsafeOnCompleted(() =>
                {
                    Interlocked.Increment(ref continuationCount);
                    continuationRan.TrySetResult();
                });
            });
            var complete = Task.Run(() =>
            {
                start.SignalAndWait();
                signal.Signal();
            });

            start.SignalAndWait();
            await Task.WhenAll(register, complete).WaitAsync(TimeSpan.FromSeconds(5));
            await continuationRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            awaiter.GetResult();
            Assert.Equal(1, continuationCount);
        }
    }

    [Fact]
    public async Task ConsumedValueTaskTokenIsRejectedAfterReset()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var staleWait = signal.WaitAsync();

        signal.Signal();
        await staleWait;

        var currentWait = signal.WaitAsync();
        Assert.Throws<InvalidOperationException>(() => _ = staleWait.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => staleWait.GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => staleWait.GetAwaiter().UnsafeOnCompleted(static () => { }));

        signal.Signal();
        await currentWait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetResultBeforeSignalIsRejectedWithoutResettingWaiter()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var wait = signal.WaitAsync();

        Assert.Throws<InvalidOperationException>(() => wait.GetAwaiter().GetResult());

        signal.Signal();
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ContinuationRunsOnOwningSchedulerAfterPreviouslyQueuedWork()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        using var queuedWorkStarted = new ManualResetEventSlim();
        using var releaseQueuedWork = new ManualResetEventSlim();
        var order = new ConcurrentQueue<int>();

        context.WorkItemGroup.QueueAction(() =>
        {
            order.Enqueue(1);
            queuedWorkStarted.Set();
            releaseQueuedWork.Wait();
        });
        Assert.True(queuedWorkStarted.Wait(TimeSpan.FromSeconds(5)));

        var observedScheduler = WaitAndCaptureScheduler(signal, order);
        signal.Signal();
        releaseQueuedWork.Set();

        var observed = await observedScheduler.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(context, observed.Context);
        Assert.Same(context.WorkItemGroup.TaskScheduler, observed.Scheduler);
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task AlternatingSynchronousAndRegisteredCyclesCompleteRepeatedly()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        using var signalRequested = new AutoResetEvent(false);
        var signaler = new Thread(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                signalRequested.WaitOne();
                signal.Signal();
            }
        })
        {
            IsBackground = true
        };
        signaler.Start();

        for (var i = 0; i < 1_000; i++)
        {
            if ((i & 1) == 0)
            {
                signal.Signal();
                await signal.WaitAsync();
            }
            else
            {
                var wait = signal.WaitAsync().AsTask();
                signalRequested.Set();
                await wait.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.True(signaler.Join(TimeSpan.FromSeconds(5)));
    }

    private static async Task<(IGrainContext? Context, TaskScheduler Scheduler)> WaitAndCaptureScheduler(
        ActivationAutoResetEvent signal,
        ConcurrentQueue<int> order)
    {
        await signal.WaitAsync();
        order.Enqueue(2);
        return (RuntimeContext.Current, TaskScheduler.Current);
    }
}
