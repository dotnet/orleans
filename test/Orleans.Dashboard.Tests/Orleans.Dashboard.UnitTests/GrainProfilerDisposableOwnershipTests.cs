using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Dashboard;
using Orleans.Dashboard.Metrics;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class GrainProfilerDisposableOwnershipTests
{
    [Fact]
    public async Task StartAndStop_CompleteSuccessfully()
    {
        var profiler = CreateProfiler();
        var observer = CaptureObserver(profiler);

        var startTask = observer.OnStart(TestContext.Current.CancellationToken);
        await startTask;
        var stopTask = observer.OnStop(TestContext.Current.CancellationToken);
        await stopTask;

        Assert.True(startTask.IsCompletedSuccessfully);
        Assert.True(stopTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAndDispose_WhenRepeated_AreIdempotent()
    {
        var profiler = CreateProfiler();
        var observer = CaptureObserver(profiler);

        var startTask = observer.OnStart(TestContext.Current.CancellationToken);
        await startTask;
        var firstStopTask = observer.OnStop(TestContext.Current.CancellationToken);
        await firstStopTask;
        var secondStopTask = observer.OnStop(TestContext.Current.CancellationToken);
        await secondStopTask;
        profiler.Dispose();
        profiler.Dispose();
        var stopAfterDisposeTask = observer.OnStop(TestContext.Current.CancellationToken);
        await stopAfterDisposeTask;

        Assert.True(startTask.IsCompletedSuccessfully);
        Assert.True(firstStopTask.IsCompletedSuccessfully);
        Assert.True(secondStopTask.IsCompletedSuccessfully);
        Assert.True(stopAfterDisposeTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StartAfterStop_CompletesSuccessfully()
    {
        var profiler = CreateProfiler();
        var observer = CaptureObserver(profiler);

        var firstStartTask = observer.OnStart(TestContext.Current.CancellationToken);
        await firstStartTask;
        var firstStopTask = observer.OnStop(TestContext.Current.CancellationToken);
        await firstStopTask;
        var secondStartTask = observer.OnStart(TestContext.Current.CancellationToken);
        await secondStartTask;
        var secondStopTask = observer.OnStop(TestContext.Current.CancellationToken);
        await secondStopTask;

        Assert.True(firstStartTask.IsCompletedSuccessfully);
        Assert.True(firstStopTask.IsCompletedSuccessfully);
        Assert.True(secondStartTask.IsCompletedSuccessfully);
        Assert.True(secondStopTask.IsCompletedSuccessfully);
    }

    private static GrainProfiler CreateProfiler() =>
        new(
            null!,
            NullLogger<GrainProfiler>.Instance,
            null!,
            Options.Create(new GrainProfilerOptions()));

    private static ILifecycleObserver CaptureObserver(GrainProfiler profiler)
    {
        var lifecycle = new CapturingLifecycle();
        profiler.Participate(lifecycle);
        return Assert.IsAssignableFrom<ILifecycleObserver>(lifecycle.Observer);
    }

    private sealed class CapturingLifecycle : ISiloLifecycle
    {
        public ILifecycleObserver? Observer { get; private set; }

        public int HighestCompletedStage => int.MinValue;

        public int LowestStoppedStage => int.MaxValue;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Observer = observer;
            return Subscription.Instance;
        }
    }

    private sealed class Subscription : IDisposable
    {
        public static Subscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
