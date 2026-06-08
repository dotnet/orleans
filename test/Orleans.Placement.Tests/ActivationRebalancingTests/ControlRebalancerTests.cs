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
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Rebalancer_Should_Be_Controllable_And_Report_To_Listeners()
    {
        var serviceProvider = Cluster.GetSiloServiceProvider();
        var rebalancer = serviceProvider.GetRequiredService<IActivationRebalancer>();

        await rebalancer.ResumeRebalancing();
        var report = await WaitForReportAsync(
            rebalancer,
            static report => report is { Status: RebalancerStatus.Executing, SuspensionDuration: null } &&
                !report.Host.Equals(SiloAddress.Zero),
            "an executing report");

        var host = report.Host;

        Assert.NotEqual(SiloAddress.Zero, host);

        // Publish-Subscribe
        var listener = new Listener();
        rebalancer.SubscribeToReports(listener);

        var reportCount = listener.Snapshot.ReportCount;

        await rebalancer.SuspendRebalancing();
        await WaitForListenerReportAsync(
            listener,
            reportCount,
            report => report.Status == RebalancerStatus.Suspended &&
                report.SuspensionDuration.HasValue &&
                report.Host.Equals(host),
            "a fresh suspended report");

        reportCount = listener.Snapshot.ReportCount;
        await rebalancer.ResumeRebalancing();
        await WaitForListenerReportAsync(
            listener,
            reportCount,
            report => report is { Status: RebalancerStatus.Executing, SuspensionDuration: null } &&
                report.Host.Equals(host),
            "a fresh executing report");

        reportCount = listener.Snapshot.ReportCount;
        await rebalancer.SuspendRebalancing();
        await WaitForListenerReportAsync(
            listener,
            reportCount,
            report => report.Status == RebalancerStatus.Suspended &&
                report.SuspensionDuration.HasValue &&
                report.Host.Equals(host),
            "a fresh suspended report before unsubscribing");

        rebalancer.UnsubscribeFromReports(listener);
        var unsubscribedSnapshot = listener.Snapshot;
        await rebalancer.ResumeRebalancing();
        await WaitForReportAsync(
            rebalancer,
            report => report is { Status: RebalancerStatus.Executing, SuspensionDuration: null } &&
                report.Host.Equals(host),
            "an executing report after unsubscribing");

        Assert.True(unsubscribedSnapshot.Report.HasValue);
        Assert.Equal(RebalancerStatus.Suspended, unsubscribedSnapshot.Report.Value.Status);
        var afterResumeSnapshot = listener.Snapshot;
        Assert.Equal(unsubscribedSnapshot.ReportCount, afterResumeSnapshot.ReportCount);
        Assert.Equal(unsubscribedSnapshot.Report, afterResumeSnapshot.Report);

        // Request-Reply
        var duration = TimeSpan.FromSeconds(5);
        await rebalancer.SuspendRebalancing(duration); // Suspend for some time
        report = await WaitForReportAsync(
            rebalancer,
            report => report.Status == RebalancerStatus.Suspended && report.SuspensionDuration.HasValue,
            "a suspended report");

        // Must be less than the time it was told to be suspended
        Assert.True(report.SuspensionDuration.Value < duration); 
        Assert.Equal(host, report.Host);

        await WaitForReportAsync(
            rebalancer,
            report => report is { Status: RebalancerStatus.Executing, SuspensionDuration: null } &&
                report.Host.Equals(host),
            "an executing report after timed suspension");

        await rebalancer.SuspendRebalancing(); // Suspend indefinitely
        await WaitForReportAsync(
            rebalancer,
            report => report.Status == RebalancerStatus.Suspended &&
                report.SuspensionDuration.HasValue &&
                report.Host.Equals(host),
            "an indefinitely suspended report");
    }

    private static async Task<RebalancingReport> WaitForReportAsync(
        IActivationRebalancer rebalancer,
        Func<RebalancingReport, bool> predicate,
        string expectedState)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        RebalancingReport report = default;
        while (DateTime.UtcNow < deadline)
        {
            report = await rebalancer.GetRebalancingReport(force: true);
            if (predicate(report))
            {
                return report;
            }

            await Task.Delay(PollInterval);
        }

        Assert.Fail($"Timed out waiting for {expectedState}. Last report: {Format(report)}");
        return report;
    }

    private static async Task<RebalancingReport> WaitForListenerReportAsync(
        Listener listener,
        int previousReportCount,
        Func<RebalancingReport, bool> predicate,
        string expectedState)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        var snapshot = listener.Snapshot;
        while (DateTime.UtcNow < deadline)
        {
            snapshot = listener.Snapshot;
            if (snapshot.ReportCount > previousReportCount &&
                snapshot.Report is { } report &&
                predicate(report))
            {
                return report;
            }

            await Task.Delay(PollInterval);
        }

        var lastReport = snapshot.Report is { } value ? Format(value) : "<none>";
        Assert.Fail(
            $"Timed out waiting for {expectedState}. Last listener report count: {snapshot.ReportCount}. Last report: {lastReport}");

        return default;
    }

    private static string Format(RebalancingReport report) =>
        $"Host={report.Host}, Status={report.Status}, SuspensionDuration={report.SuspensionDuration?.ToString() ?? "<null>"}";

    private class Listener : IActivationRebalancerReportListener
    {
        private readonly object _lock = new();
        private RebalancingReport? _report;
        private int _reportCount;

        public (int ReportCount, RebalancingReport? Report) Snapshot
        {
            get
            {
                lock (_lock)
                {
                    return (_reportCount, _report);
                }
            }
        }

        public void OnReport(RebalancingReport report)
        {
            lock (_lock)
            {
                _report = report;
                _reportCount++;
            }
        }
    }
}
