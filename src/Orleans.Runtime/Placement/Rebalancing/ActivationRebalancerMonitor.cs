using System;
using Orleans.Runtime.Placement.Repartitioning;
using System.Threading.Tasks;
using Orleans.Placement.Rebalancing;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using Orleans.Runtime.Scheduler;

#nullable enable

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed partial class ActivationRebalancerMonitor : SystemTarget, IActivationRebalancerMonitor, ILifecycleParticipant<ISiloLifecycle>
{
    private IGrainTimer? _monitorTimer;
    private RebalancingReport _latestReport;
    private long _lastHeartbeatTimestamp;

    private readonly TimeProvider _timeProvider;
    private readonly ActivationDirectory _activationDirectory;
    private readonly IActivationRebalancerWorker _rebalancerGrain;
    private readonly ILogger<ActivationRebalancerMonitor> _logger;
    private readonly List<IActivationRebalancerReportListener> _statusListeners = [];

    // Check on the worker with double the period the worker reports to me.
    private readonly static TimeSpan TimerPeriod = 2 * IActivationRebalancerMonitor.WorkerReportPeriod;

    public ActivationRebalancerMonitor(
        TimeProvider timeProvider,
        ActivationDirectory activationDirectory,
        ILoggerFactory loggerFactory,
        IGrainFactory grainFactory,
        SystemTargetShared shared)
        : base(Constants.ActivationRebalancerMonitorType, shared)
    {
        _timeProvider = timeProvider;
        _activationDirectory = activationDirectory;
        _logger = loggerFactory.CreateLogger<ActivationRebalancerMonitor>();
        _rebalancerGrain = grainFactory.GetGrain<IActivationRebalancerWorker>(0);
        _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();

        _latestReport = new()
        {
            ClusterImbalance = 1,
            Host = SiloAddress.Zero,
            Status = RebalancerStatus.Suspended,
            SuspensionDuration = Timeout.InfiniteTimeSpan,
            Statistics = []
        };
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
           nameof(ActivationRepartitioner),
           ServiceLifecycleStage.Active,
           OnStart,
           _ => Task.CompletedTask);

        observer.Subscribe(
           nameof(ActivationRepartitioner),
           ServiceLifecycleStage.ApplicationServices,
           _ => Task.CompletedTask,
           OnStop);
    }

    private async Task OnStart(CancellationToken cancellationToken)
    {
        await this.RunOrQueueTask(async () =>
        {
            _monitorTimer = RegisterGrainTimer(async ct =>
            {
                var elapsedSinceHeartbeat = _timeProvider.GetElapsedTime(_lastHeartbeatTimestamp);
                if (elapsedSinceHeartbeat >= IActivationRebalancerMonitor.WorkerReportPeriod)
                {
                    LogStartingRebalancer(elapsedSinceHeartbeat, IActivationRebalancerMonitor.WorkerReportPeriod);
                    _latestReport = await _rebalancerGrain.GetReport().AsTask().WaitAsync(ct);
                }

            }, TimerPeriod, TimerPeriod);

            _latestReport = await _rebalancerGrain.GetReport().AsTask().WaitAsync(cancellationToken);
        });
    }

    private async Task OnStop(CancellationToken cancellationToken)
    {
        await this.RunOrQueueTask(() =>
        {
            if (_latestReport is { } report && Silo.IsSameLogicalSilo(report.Host))
            {
                if (_activationDirectory.FindTarget(_rebalancerGrain.GetGrainId()) is { } activation)
                {
                    LogMigratingRebalancer(Silo);
                    activation.Migrate(null, cancellationToken); // migrate it anywhere else
                }
            }

            _monitorTimer?.Dispose();
            return Task.CompletedTask;
        });
    }

    public Task ResumeRebalancing() => _rebalancerGrain.ResumeRebalancing();
    public Task SuspendRebalancing(TimeSpan? duration) => _rebalancerGrain.SuspendRebalancing(duration);

    public async ValueTask<RebalancingReport> GetRebalancingReport(bool force = false)
    {
        if (force)
        {
            _latestReport = await _rebalancerGrain.GetReport();
        }

        return _latestReport;
    }

    public Task Report(RebalancingReport report)
    {
        _latestReport = report;
        _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();

        foreach (var listener in _statusListeners)
        {
            try
            {
                listener.OnReport(report);
            }
            catch (Exception ex)
            {
                LogErrorWhileNotifyingListener(ex);
            }
        }

        return Task.CompletedTask;
    }

    public void SubscribeToReports(IActivationRebalancerReportListener listener)
    {
        if (!_statusListeners.Contains(listener))
        {
            _statusListeners.Add(listener);
        }
    }

    public void UnsubscribeFromReports(IActivationRebalancerReportListener listener) =>
        _statusListeners.Remove(listener);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "I have not received a report from the activation rebalancer for the last {Duration} which is more than the " +
        "allowed interval {Period}. I will now try to wake it up with the assumption that it has has been stopped ungracefully."
    )]
    private partial void LogStartingRebalancer(TimeSpan duration, TimeSpan period);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "My silo '{Silo}' is stopping now, and I am the host of the activation rebalancer. " +
        "I will attempt to migrate the rebalancer to another silo."
    )]
    private partial void LogMigratingRebalancer(SiloAddress silo);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An unexpected error occurred while notifying rebalancer listener."
    )]
    private partial void LogErrorWhileNotifyingListener(Exception exception);
}
