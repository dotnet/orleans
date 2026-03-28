using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement.Rebalancing;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for controlling the activation rebalancer, including suspend/resume operations and report subscription.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class ControlRebalancerTests(RebalancerFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<RebalancerFixture>(fixture, output), IClassFixture<RebalancerFixture>
{
    [Fact]
    public async Task Rebalancer_Should_Be_Controllable_And_Report_To_Listeners()
    {
        var serviceProvider = Cluster.GetSiloServiceProvider();
        var rebalancer = serviceProvider.GetRequiredService<IActivationRebalancer>();
        var report = await rebalancer.GetRebalancingReport();
        var host = report.Host;

        Assert.Equal(RebalancerStatus.Executing, report.Status);
        Assert.Null(report.SuspensionDuration);
        Assert.NotEqual(SiloAddress.Zero, host);

        // Publish-Subscribe
        var listener = new Listener();
        rebalancer.SubscribeToReports(listener);
        Assert.False(listener.Report.HasValue);

        await rebalancer.ResumeRebalancing();
        Assert.True(listener.Report.HasValue);
        Assert.Equal(RebalancerStatus.Executing, listener.Report.Value.Status);
        Assert.Equal(host, listener.Report.Value.Host);

        await rebalancer.SuspendRebalancing();
        Assert.Equal(RebalancerStatus.Suspended, listener.Report.Value.Status);
        Assert.True(listener.Report.Value.SuspensionDuration.HasValue);
        Assert.Equal(host, listener.Report.Value.Host);

        rebalancer.UnsubscribeFromReports(listener);
        await rebalancer.ResumeRebalancing();
        while (report.Status == RebalancerStatus.Suspended)
        {
            report = await rebalancer.GetRebalancingReport(true);
            await Task.Delay(100);
        }
        // Its actually resumed, but here its still suspended since we unsubscribed
        Assert.Equal(RebalancerStatus.Suspended, listener.Report.Value.Status); 

        // Request-Reply
        var duration = TimeSpan.FromSeconds(5);
        await rebalancer.SuspendRebalancing(duration); // Suspend for some time
        while (report.Status == RebalancerStatus.Executing)
        {
            report = await rebalancer.GetRebalancingReport(true);
            await Task.Delay(100);
        }

        Assert.True(report.SuspensionDuration.HasValue);
        // Must be less than the time it was told to be suspended
        Assert.True(report.SuspensionDuration.Value < duration); 
        Assert.Equal(host, report.Host);

        while (report.Status == RebalancerStatus.Suspended)
        {
            report = await rebalancer.GetRebalancingReport(true);
            await Task.Delay(100);
        }

        report = await rebalancer.GetRebalancingReport(true);
        Assert.False(report.SuspensionDuration.HasValue);
        Assert.Equal(host, report.Host);

        await rebalancer.SuspendRebalancing(); // Suspend indefinitely
        while (report.Status == RebalancerStatus.Executing)
        {
            report = await rebalancer.GetRebalancingReport(true);
            await Task.Delay(100);
        }
        report = await rebalancer.GetRebalancingReport(true);
        Assert.True(report.SuspensionDuration.HasValue);
        Assert.Equal(host, report.Host);
    }

    private class Listener : IActivationRebalancerReportListener
    {
        public RebalancingReport? Report { get; private set; }
        public void OnReport(RebalancingReport report) => Report = report;
    }
}