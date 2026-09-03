using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Dashboard;
using Orleans.Dashboard.Metrics;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class GrainProfilerTests
{
    [Fact]
    public async Task CanceledStart_RemainsStopSafe()
    {
        var profiler = new GrainProfiler(
            null!,
            NullLogger<GrainProfiler>.Instance,
            null!,
            Options.Create(new GrainProfilerOptions()));
        var lifecycle = new CapturingLifecycle();

        profiler.Participate(lifecycle);
        var registeredObserver = Assert.IsAssignableFrom<ILifecycleObserver>(lifecycle.Observer);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var startTask = registeredObserver.OnStart(cancellation.Token);
        await startTask;
        var stopTask = registeredObserver.OnStop(TestContext.Current.CancellationToken);
        await stopTask;

        Assert.True(startTask.IsCompletedSuccessfully);
        Assert.True(stopTask.IsCompletedSuccessfully);
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
