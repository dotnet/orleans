#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using UnitTests.TesterInternal;
using Xunit;

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

    [Fact]
    public async Task ContinuationRunsOnScheduler()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);
        var observedContext = new TaskCompletionSource<IGrainContext?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = WaitAndCaptureContext(signal, observedContext);
        signal.Signal();

        Assert.Same(context, await observedContext.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAndSignalRaceCompletesRepeatedly()
    {
        using var context = UnitTestSchedulingContext.Create(NullLoggerFactory.Instance);
        var signal = new ActivationAutoResetEvent(context.WorkItemGroup);

        for (var i = 0; i < 1_000; i++)
        {
            var wait = signal.WaitAsync();
            _ = Task.Run(signal.Signal);
            await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WaitAndCaptureContext(ActivationAutoResetEvent signal, TaskCompletionSource<IGrainContext?> observedContext)
    {
        await signal.WaitAsync();
        observedContext.SetResult(RuntimeContext.Current);
    }
}
