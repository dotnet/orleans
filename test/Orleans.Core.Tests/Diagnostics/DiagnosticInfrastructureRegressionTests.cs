using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using Orleans.TestingHost.Logging;
using TestExtensions;
using Xunit;

namespace UnitTests.Diagnostics;

public class DiagnosticInfrastructureRegressionTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task DiagnosticEventCollector_PredicateTimeout_DoesNotBlockSubsequentWaits()
    {
        using var collector = new DiagnosticEventCollector("Orleans.Test.");
        using var listener = new DiagnosticListener("Orleans.Test.Collector");

        var waitTask = collector.WaitForEventAsync(
            "Value",
            evt => evt.Payload is int value && value == 2,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);

        listener.Write("Value", 1);

        await Assert.ThrowsAsync<TimeoutException>(() => waitTask);

        var secondWaitTask = collector.WaitForEventAsync(
            "Value",
            evt => evt.Payload is int value && value == 2,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        listener.Write("Value", 2);

        var result = await secondWaitTask;
        Assert.Equal(2, Assert.IsType<int>(result.Payload));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task GrainDiagnosticObserver_WaitForAnyGrainDeactivatedAsync_TimesOut()
    {
        using var observer = GrainDiagnosticObserver.CreateForAllSilos();

        await Assert.ThrowsAsync<TimeoutException>(() => observer.WaitForAnyGrainDeactivatedAsync(_ => false, TimeSpan.FromMilliseconds(100)));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task GrainDiagnosticObserver_WaitAfterTimeout_CanObserveLaterEvent()
    {
        using var observer = GrainDiagnosticObserver.CreateForAllSilos();
        var grainId = GrainId.Create("test", "grain-1");

        await Assert.ThrowsAsync<TimeoutException>(() => observer.WaitForGrainCreatedAsync(grainId, TimeSpan.FromMilliseconds(100)));

        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(grainId);

        GrainLifecycleEvents.EmitCreated(grainContext);

        var created = await observer.WaitForGrainCreatedAsync(grainId, TimeSpan.FromSeconds(1));
        Assert.Same(grainContext, created.GrainContext);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task GrainAndTimerDiagnosticObservers_IgnoreOtherSilos()
    {
        var targetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11990), 1);
        var otherSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11991), 1);
        var grainId = GrainId.Create("test", "shared-grain");
        var targetContext = CreateGrainContext(targetSilo, grainId);
        var otherContext = CreateGrainContext(otherSilo, grainId);
        var timer = Substitute.For<IGrainTimer>();
        using var grainObserver = GrainDiagnosticObserver.Create(targetSilo);
        using var timerObserver = TimerDiagnosticObserver.Create(targetSilo);

        var grainWait = grainObserver.WaitForGrainCreatedAsync(grainId, TimeSpan.FromSeconds(1));
        var timerWait = timerObserver.WaitForTimerCreatedAsync(grainId, TimeSpan.FromSeconds(1));
        GrainLifecycleEvents.EmitCreated(otherContext);
        GrainTimerEvents.EmitCreated(otherContext, TimeSpan.Zero, TimeSpan.FromSeconds(1), timer);

        Assert.False(grainWait.IsCompleted);
        Assert.False(timerWait.IsCompleted);

        GrainLifecycleEvents.EmitCreated(targetContext);
        GrainTimerEvents.EmitCreated(targetContext, TimeSpan.Zero, TimeSpan.FromSeconds(1), timer);

        Assert.Same(targetContext, (await grainWait).GrainContext);
        Assert.Same(targetContext, (await timerWait).GrainContext);
        Assert.Single(grainObserver.CreatedEvents);
        Assert.Single(timerObserver.CreatedEvents);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_WaitForCycleAsync_ReturnsNewEvent()
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12000), 1);
        using var observer = RebalancerDiagnosticObserver.Create(siloAddress);

        ActivationRebalancerEvents.EmitCycleStop(siloAddress, 1, 1, 0.1, TimeSpan.FromMilliseconds(1), false);

        var waitTask = observer.WaitForCycleAsync();
        Assert.False(waitTask.IsCompleted);

        ActivationRebalancerEvents.EmitCycleStop(siloAddress, 2, 2, 0.2, TimeSpan.FromMilliseconds(1), false);
        Assert.True(waitTask.IsCompletedSuccessfully);

        var result = await waitTask;
        Assert.Equal(2, result.CycleNumber);
        Assert.Equal(2, result.ActivationsMigrated);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_IgnoresOtherSilos()
    {
        var targetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12010), 1);
        var otherSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12011), 1);
        using var observer = RebalancerDiagnosticObserver.Create(targetSilo);

        var waitTask = observer.WaitForSessionStopAsync(TimeSpan.FromSeconds(1));
        ActivationRebalancerEvents.EmitSessionStop(otherSilo, "other", 1);

        Assert.False(waitTask.IsCompleted);

        ActivationRebalancerEvents.EmitSessionStop(targetSilo, "target", 2);

        var result = await waitTask;
        Assert.Equal(targetSilo, result.SiloAddress);
        Assert.Single(observer.SessionStopEvents);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_WaitForSessionStopAsync_ReturnsNewEvent()
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12001), 2);
        using var observer = RebalancerDiagnosticObserver.Create(siloAddress);

        ActivationRebalancerEvents.EmitSessionStop(siloAddress, "existing", 1);

        var waitTask = observer.WaitForSessionStopAsync();
        Assert.False(waitTask.IsCompleted);

        ActivationRebalancerEvents.EmitSessionStop(siloAddress, "latest", 2);
        Assert.True(waitTask.IsCompletedSuccessfully);

        var result = await waitTask;
        Assert.Equal("latest", result.Reason);
        Assert.Equal(2, result.TotalCycles);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_WaitAfterTimeout_CanObserveLaterEvent()
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12002), 3);
        using var observer = RebalancerDiagnosticObserver.Create(siloAddress);

        var timedOutWaitTask = observer.WaitForSessionStopAsync(TimeSpan.Zero);
        Assert.True(timedOutWaitTask.IsCompleted);
        await Assert.ThrowsAsync<TimeoutException>(() => timedOutWaitTask);

        var timedOutCountWaitTask = observer.WaitForSessionStopCountAsync(1, TimeSpan.Zero);
        Assert.True(timedOutCountWaitTask.IsCompleted);
        await Assert.ThrowsAsync<TimeoutException>(() => timedOutCountWaitTask);

        var waitTask = observer.WaitForSessionStopAsync();
        ActivationRebalancerEvents.EmitSessionStop(siloAddress, "latest", 1);
        Assert.True(waitTask.IsCompletedSuccessfully);

        var result = await waitTask;
        Assert.Equal("latest", result.Reason);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_Dispose_CompletesOutstandingWaiters()
    {
        var observer = RebalancerDiagnosticObserver.Create(
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12003), 4));
        var waitTask = observer.WaitForSessionStopAsync();

        observer.Dispose();

        Assert.True(waitTask.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waitTask);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void InMemoryLoggerProvider_FormatsStoredThreadId()
    {
        var buffer = new InMemoryLogBuffer();
        using var provider = new InMemoryLoggerProvider(buffer);
        var logger = provider.CreateLogger("Test.Category");
        var loggedThreadId = 0;

        var thread = new Thread(() =>
        {
            loggedThreadId = Environment.CurrentManagedThreadId;
            logger.Log(LogLevel.Information, new EventId(7, "test"), "hello", exception: null, static (state, _) => state);
        });

        thread.Start();
        thread.Join();

        var formatted = buffer.FormatAllEntries().TrimEnd();
        var threadSegment = formatted[..formatted.IndexOf('\t')];
        var actualThreadId = threadSegment[(threadSegment.LastIndexOf(' ') + 1)..];

        Assert.Equal(loggedThreadId.ToString(CultureInfo.InvariantCulture), actualThreadId);
    }

    private static IGrainContext CreateGrainContext(SiloAddress siloAddress, GrainId grainId)
    {
        var result = Substitute.For<IGrainContext>();
        result.GrainId.Returns(grainId);
        result.Address.Returns(GrainAddress.NewActivationAddress(siloAddress, grainId));
        return result;
    }
}
