using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement.Rebalancing;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for controlling the activation rebalancer, including suspend/resume operations and report subscription.
/// Uses event-driven waiting via AsyncListener instead of polling loops.
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

        // Publish-Subscribe with simple listener
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

        // Unsubscribe and use async listener for remaining tests
        rebalancer.UnsubscribeFromReports(listener);
        
        // Use AsyncListener to wait for status changes without polling
        using var asyncListener = new AsyncListener();
        rebalancer.SubscribeToReports(asyncListener);
        
        await rebalancer.ResumeRebalancing();
        report = await asyncListener.WaitForStatusAsync(RebalancerStatus.Executing, TimeSpan.FromSeconds(10));
        Assert.Equal(RebalancerStatus.Executing, report.Status);
        // The simple listener still shows Suspended since it was unsubscribed
        Assert.Equal(RebalancerStatus.Suspended, listener.Report.Value.Status);

        // Request-Reply: suspend for a duration and verify suspension
        var duration = TimeSpan.FromSeconds(5);
        await rebalancer.SuspendRebalancing(duration);
        report = await asyncListener.WaitForStatusAsync(RebalancerStatus.Suspended, TimeSpan.FromSeconds(10));

        Assert.True(report.SuspensionDuration.HasValue);
        // Must be less than the time it was told to be suspended
        Assert.True(report.SuspensionDuration.Value < duration);
        Assert.Equal(host, report.Host);

        // Wait for automatic resume after suspension expires
        report = await asyncListener.WaitForStatusAsync(RebalancerStatus.Executing, duration + TimeSpan.FromSeconds(5));
        Assert.False(report.SuspensionDuration.HasValue);
        Assert.Equal(host, report.Host);

        // Suspend indefinitely and verify
        await rebalancer.SuspendRebalancing();
        report = await asyncListener.WaitForStatusAsync(RebalancerStatus.Suspended, TimeSpan.FromSeconds(10));
        Assert.True(report.SuspensionDuration.HasValue);
        Assert.Equal(host, report.Host);
        
        rebalancer.UnsubscribeFromReports(asyncListener);
    }

    private class Listener : IActivationRebalancerReportListener
    {
        public RebalancingReport? Report { get; private set; }
        public void OnReport(RebalancingReport report) => Report = report;
    }
    
    /// <summary>
    /// An async-capable listener that allows waiting for specific status changes.
    /// This enables event-driven waiting instead of polling loops.
    /// </summary>
    private sealed class AsyncListener : IActivationRebalancerReportListener, IDisposable
    {
        private readonly object _lock = new();
        private RebalancingReport? _report;
        private TaskCompletionSource<RebalancingReport>? _waiter;
        private RebalancerStatus? _waitingForStatus;
        
        public RebalancingReport? Report
        {
            get { lock (_lock) return _report; }
        }
        
        public void OnReport(RebalancingReport report)
        {
            TaskCompletionSource<RebalancingReport>? waiterToComplete = null;
            
            lock (_lock)
            {
                _report = report;
                
                // If someone is waiting for this specific status, complete their task
                if (_waiter != null && _waitingForStatus.HasValue && report.Status == _waitingForStatus.Value)
                {
                    waiterToComplete = _waiter;
                    _waiter = null;
                    _waitingForStatus = null;
                }
            }
            
            waiterToComplete?.TrySetResult(report);
        }
        
        /// <summary>
        /// Waits for the rebalancer to report a specific status.
        /// </summary>
        public async Task<RebalancingReport> WaitForStatusAsync(RebalancerStatus status, TimeSpan timeout)
        {
            TaskCompletionSource<RebalancingReport> tcs;
            
            lock (_lock)
            {
                // If we already have the expected status, return immediately
                if (_report.HasValue && _report.Value.Status == status)
                {
                    return _report.Value;
                }
                
                // Create a new waiter
                tcs = new TaskCompletionSource<RebalancingReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = tcs;
                _waitingForStatus = status;
            }
            
            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
            
            return await tcs.Task;
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                _waiter?.TrySetCanceled();
                _waiter = null;
            }
        }
    }
}