using System;
using Orleans.Runtime.Placement.Repartitioning;
using System.Threading.Tasks;
using Orleans.Placement.Rebalancing;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed partial class ActivationRebalancerMonitor : SystemTarget, IActivationRebalancerMonitor, ILifecycleParticipant<ISiloLifecycle>
{
    private SiloAddress? _rebalancerAddress;
    private IGrainTimer? _rebalancerTimer;
    private DateTime _lastHartbeat = DateTime.MinValue;
    private ImmutableArray<RebalancingStatistics> _lastStatistics;

    private readonly TimeProvider _timeProvider;
    private readonly ActivationDirectory _activationDirectory;
    private readonly ISiloStatusOracle _siloStatusOracle;
    private readonly IActivationRebalancerWorker _rebalancerGrain;
    private readonly ILogger<ActivationRebalancerMonitor> _logger;

    // Check on the worker with double the period the worker reports to me
    private readonly static TimeSpan TimerPeriod = 2 * IActivationRebalancerMonitor.WorkerReportPeriod;

    public ActivationRebalancerMonitor(
        Catalog catalog,
        TimeProvider timeProvider,
        ActivationDirectory activationDirectory,
        ILoggerFactory loggerFactory,
        IGrainFactory grainFactory,
        ILocalSiloDetails localSiloDetails,
        ISiloStatusOracle siloStatusOracle)
            : base(Constants.ActivationRebalancerMonitorType, localSiloDetails.SiloAddress, loggerFactory)
    {
        _rebalancerGrain = grainFactory.GetGrain<IActivationRebalancerWorker>(0);
        _timeProvider = timeProvider;
        _activationDirectory = activationDirectory;
        _siloStatusOracle = siloStatusOracle;
        _logger = loggerFactory.CreateLogger<ActivationRebalancerMonitor>();
        catalog.RegisterSystemTarget(this);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
           nameof(ActivationRepartitioner),
           ServiceLifecycleStage.Active,
           _ => OnStart(),
           _ => Task.CompletedTask);

        observer.Subscribe(
           nameof(ActivationRepartitioner),
           ServiceLifecycleStage.ApplicationServices,
           _ => Task.CompletedTask,
           _ => OnStop());
    }  

    private async Task OnStart()
    {
        _rebalancerTimer = RegisterTimer(async _ =>
        {
            var now = _timeProvider.GetUtcNow().DateTime;
            if (now > _lastHartbeat.Add(IActivationRebalancerMonitor.WorkerReportPeriod))
            {
                LogStartingRebalancer(now - _lastHartbeat, IActivationRebalancerMonitor.WorkerReportPeriod);
                _rebalancerAddress = await _rebalancerGrain.StartRebalancer();
            }

        }, null, TimerPeriod, TimerPeriod);

        _rebalancerAddress = await _rebalancerGrain.StartRebalancer();
    }

    private Task OnStop()
    {
        if (Silo.IsSameLogicalSilo(_rebalancerAddress))
        {
            if (_activationDirectory.FindTarget(_rebalancerGrain.GetGrainId()) is { } activation)
            {
                LogMigratingRebalancer(Silo);
                activation.Migrate(null); // migrate it anywhere else
            }
        }

        _rebalancerTimer?.Dispose();
        return Task.CompletedTask;
    }

    public Task ResumeRebalancing() => _rebalancerGrain.ResumeRebalancing();
    public Task SuspendRebalancing(TimeSpan? duration) => _rebalancerGrain.SuspendRebalancing(duration);

    public async ValueTask<ImmutableArray<RebalancingStatistics>> GetStatistics(bool force)
    {
        if (force)
        {
            _lastStatistics = await _rebalancerGrain.GetStatistics();
        }

        return _lastStatistics;
    }

    public Task Report(SiloAddress address, ImmutableArray<RebalancingStatistics> statistics)
    {
        _rebalancerAddress = address;
        _lastStatistics = statistics;
        _lastHartbeat = _timeProvider.GetUtcNow().DateTime;

        return Task.CompletedTask;
    }

    public ValueTask<SiloAddress> GetRebalancerHost() => new(_rebalancerAddress ?? SiloAddress.Zero);

    [LoggerMessage(Level = LogLevel.Trace, Message =
        "I have not received a report from the activation rebalancer for the last {Duration} which is more than the " +
        "allowed interval {Period}. I will now try to wake it up with the assumption that it has has been stopped ungracefully.")]
    private partial void LogStartingRebalancer(TimeSpan duration, TimeSpan period);

    [LoggerMessage(Level = LogLevel.Trace, Message =
        "My silo '{Silo}' is stopping now, and I am the host of the activation rebalancer. " +
        "I will attempt to migrate the rebalancer to another silo.")]
    private partial void LogMigratingRebalancer(SiloAddress silo);
} 