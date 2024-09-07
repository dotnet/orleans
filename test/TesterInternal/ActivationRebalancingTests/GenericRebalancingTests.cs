using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement.Rebalancing;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class GenericRebalancingTests(RebalancerFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<RebalancerFixture>(fixture, output), IClassFixture<RebalancerFixture>
{
    private readonly RebalancerFixture _fixture = fixture;

    [Fact]
    public async Task Should_Be_Able_To_Control_The_Rebalancer()
    {
        var rebalancer = _fixture.HostedCluster.ServiceProvider.GetRequiredService<IActivationRebalancer>();

        var report = await rebalancer.GetRebalancingReport();

        Assert.NotEqual(SiloAddress.Zero, report.Silo);
        Assert.Equal(RebalancerStatus.Executing, report.Status);
        Assert.Null(report.SuspensionDuration);

        var listener = new StatusListener();
        rebalancer.SubscribeToReports(listener);

        await rebalancer.ResumeRebalancing();
        Assert.True(listener.Report.HasValue);
        Assert.Equal(RebalancerStatus.Executing, listener.Report.Value.Status);

        var timespan = TimeSpan.FromSeconds(10);
        var delay = TimeSpan.FromSeconds(3);

        await rebalancer.SuspendRebalancing(timespan);
        await Task.Delay(delay);

        Assert.Equal(RebalancerStatus.Suspended, listener.Report.Value.Status);
        Assert.True(listener.Report.Value.SuspensionDuration.HasValue);
        Assert.True(listener.Report.Value.SuspensionDuration <= timespan - delay);

        await rebalancer.ResumeRebalancing();
        Assert.Equal(RebalancerStatus.Executing, listener.Report.Value.Status);

        await rebalancer.SuspendRebalancing();
        Assert.Equal(RebalancerStatus.Suspended, listener.Report.Value.Status);
        Assert.False(listener.Report.Value.SuspensionDuration.HasValue);
    }

    private class StatusListener : IActivationRebalancerReportListener
    {
        public RebalancingReport? Report { get; private set; }
        public void OnReport(RebalancingReport report) => Report = report;
    }
}
