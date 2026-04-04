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
using Orleans.TestingHost.Logging;
using TestExtensions;
using Xunit;

namespace UnitTests.Diagnostics;

public class DiagnosticInfrastructureRegressionTests
{
    [Fact, TestCategory("BVT")]
    public async Task GrainDiagnosticObserver_WaitForAnyGrainDeactivatedAsync_TimesOut()
    {
        using var observer = GrainDiagnosticObserver.Create();

        await Assert.ThrowsAsync<TimeoutException>(() => observer.WaitForAnyGrainDeactivatedAsync(_ => false, TimeSpan.FromMilliseconds(100)));
    }

    [Fact, TestCategory("BVT")]
    public async Task GrainDiagnosticObserver_WaitAfterTimeout_CanObserveLaterEvent()
    {
        using var observer = GrainDiagnosticObserver.Create();
        var grainId = GrainId.Create("test", "grain-1");

        await Assert.ThrowsAsync<TimeoutException>(() => observer.WaitForGrainCreatedAsync(grainId, TimeSpan.FromMilliseconds(100)));

        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(grainId);

        GrainLifecycleEvents.EmitCreated(grainContext);

        var created = await observer.WaitForGrainCreatedAsync(grainId, TimeSpan.FromSeconds(1));
        Assert.Same(grainContext, created.GrainContext);
    }

    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_WaitForCycleAsync_ReturnsNewEvent()
    {
        using var observer = RebalancerDiagnosticObserver.Create();
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12000), 1);

        ActivationRebalancerEvents.EmitCycleStop(siloAddress, 1, 1, 0.1, TimeSpan.FromMilliseconds(1), false);

        var waitTask = observer.WaitForCycleAsync(TimeSpan.FromSeconds(1));
        ActivationRebalancerEvents.EmitCycleStop(siloAddress, 2, 2, 0.2, TimeSpan.FromMilliseconds(1), false);

        var result = await waitTask;
        Assert.Equal(2, result.CycleNumber);
        Assert.Equal(2, result.ActivationsMigrated);
    }

    [Fact, TestCategory("BVT")]
    public async Task RebalancerDiagnosticObserver_WaitForSessionStopAsync_ReturnsNewEvent()
    {
        using var observer = RebalancerDiagnosticObserver.Create();
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12001), 2);

        ActivationRebalancerEvents.EmitSessionStop(siloAddress, "existing", 1);

        var waitTask = observer.WaitForSessionStopAsync(TimeSpan.FromSeconds(1));
        ActivationRebalancerEvents.EmitSessionStop(siloAddress, "latest", 2);

        var result = await waitTask;
        Assert.Equal("latest", result.Reason);
        Assert.Equal(2, result.TotalCycles);
    }

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
}
