using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement.Rebalancing;
using TestExtensions;
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

    [Fact]
    public async Task Rebalancer_Should_Be_Controllable_And_Report_To_Listeners()
    {
        var serviceProvider = Cluster.GetSiloServiceProvider();
        var rebalancer = serviceProvider.GetRequiredService<IActivationRebalancer>();
        using var rebalancerEvents = RebalancerDiagnosticObserver.Create();

        var sessionStarted = rebalancerEvents.WaitForSessionStartAsync(WaitTimeout);
        await rebalancer.ResumeRebalancing();
        var host = (await sessionStarted).SiloAddress;
        var report = await rebalancer.GetRebalancingReport();

        Assert.NotEqual(SiloAddress.Zero, host);
        Assert.Equal(RebalancerStatus.Executing, report.Status);
        Assert.Null(report.SuspensionDuration);
        Assert.Equal(host, report.Host);

        // Publish-Subscribe
        var listener = new Listener();
        rebalancer.SubscribeToReports(listener);
        Assert.Equal(0, listener.Snapshot.ReportCount);

        var reportCount = listener.Snapshot.ReportCount;

        var listenerReport = listener.WaitForReportAsync(
            reportCount,
            report => report.Status == RebalancerStatus.Suspended &&
                report.SuspensionDuration.HasValue &&
                report.Host.Equals(host),
            "a fresh suspended report");
        await rebalancer.SuspendRebalancing();
        await listenerReport;

        reportCount = listener.Snapshot.ReportCount;
        var resumed = rebalancerEvents.WaitForSessionStartAsync(
            sessionStart => sessionStart.SiloAddress.Equals(host),
            WaitTimeout);
        listenerReport = listener.WaitForReportAsync(
            reportCount,
            report => report is { Status: RebalancerStatus.Executing, SuspensionDuration: null } &&
                report.Host.Equals(host),
            "a fresh executing report");
        await rebalancer.ResumeRebalancing();
        await resumed;
        await listenerReport;

        reportCount = listener.Snapshot.ReportCount;
        listenerReport = listener.WaitForReportAsync(
            reportCount,
            report => report.Status == RebalancerStatus.Suspended &&
                report.SuspensionDuration.HasValue &&
                report.Host.Equals(host),
            "a fresh suspended report before unsubscribing");
        await rebalancer.SuspendRebalancing();
        await listenerReport;

        rebalancer.UnsubscribeFromReports(listener);
        var unsubscribedSnapshot = listener.Snapshot;
        resumed = rebalancerEvents.WaitForSessionStartAsync(
            sessionStart => sessionStart.SiloAddress.Equals(host),
            WaitTimeout);
        await rebalancer.ResumeRebalancing();
        await resumed;

        Assert.True(unsubscribedSnapshot.Report.HasValue);
        Assert.Equal(RebalancerStatus.Suspended, unsubscribedSnapshot.Report.Value.Status);
        var afterResumeSnapshot = listener.Snapshot;
        Assert.Equal(unsubscribedSnapshot.ReportCount, afterResumeSnapshot.ReportCount);
        Assert.Equal(unsubscribedSnapshot.Report, afterResumeSnapshot.Report);

        // Request-Reply
        var duration = TimeSpan.FromSeconds(2);
        await rebalancer.SuspendRebalancing(duration); // Suspend for some time
        report = await rebalancer.GetRebalancingReport();

        Assert.Equal(RebalancerStatus.Suspended, report.Status);
        Assert.True(report.SuspensionDuration.HasValue);
        // Must be less than the time it was told to be suspended
        Assert.True(report.SuspensionDuration.Value < duration); 
        Assert.Equal(host, report.Host);

        resumed = rebalancerEvents.WaitForSessionStartAsync(
            sessionStart => sessionStart.SiloAddress.Equals(host),
            WaitTimeout);
        await resumed;

        await rebalancer.SuspendRebalancing(); // Suspend indefinitely
        report = await rebalancer.GetRebalancingReport();
        Assert.Equal(RebalancerStatus.Suspended, report.Status);
        Assert.True(report.SuspensionDuration.HasValue);
        Assert.Equal(host, report.Host);
    }

    private static string Format(RebalancingReport report) =>
        $"Host={report.Host}, Status={report.Status}, SuspensionDuration={report.SuspensionDuration?.ToString() ?? "<null>"}";

    private class Listener : IActivationRebalancerReportListener
    {
        private readonly object _lock = new();
        private readonly List<ReportWaiter> _waiters = [];
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

        public Task<RebalancingReport> WaitForReportAsync(
            int previousReportCount,
            Func<RebalancingReport, bool> predicate,
            string expectedState)
        {
            lock (_lock)
            {
                if (_reportCount > previousReportCount &&
                    _report is { } report &&
                    predicate(report))
                {
                    return Task.FromResult(report);
                }

                var waiter = new ReportWaiter(previousReportCount, predicate);
                _waiters.Add(waiter);
                return WaitWithTimeoutAsync(waiter, expectedState);
            }
        }

        public void OnReport(RebalancingReport report)
        {
            lock (_lock)
            {
                _report = report;
                _reportCount++;

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].TryComplete(_reportCount, report))
                    {
                        _waiters.RemoveAt(i);
                    }
                }
            }
        }

        private async Task<RebalancingReport> WaitWithTimeoutAsync(ReportWaiter waiter, string expectedState)
        {
            try
            {
                return await waiter.Task.WaitAsync(WaitTimeout);
            }
            catch (TimeoutException)
            {
                lock (_lock)
                {
                    _waiters.Remove(waiter);
                }

                var snapshot = Snapshot;
                var lastReport = snapshot.Report is { } value ? Format(value) : "<none>";
                throw new TimeoutException(
                    $"Timed out waiting for {expectedState}. Last listener report count: {snapshot.ReportCount}. Last report: {lastReport}");
            }
        }

        private sealed class ReportWaiter(int previousReportCount, Func<RebalancingReport, bool> predicate)
        {
            private readonly TaskCompletionSource<RebalancingReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<RebalancingReport> Task => _completion.Task;

            public bool TryComplete(int reportCount, RebalancingReport report)
            {
                if (reportCount <= previousReportCount || !predicate(report))
                {
                    return false;
                }

                return _completion.TrySetResult(report);
            }
        }
    }
}
