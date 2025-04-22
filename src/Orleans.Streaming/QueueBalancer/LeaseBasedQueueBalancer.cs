using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Runtime.Internal;

namespace Orleans.Streams;

/// <summary>
/// LeaseBasedQueueBalancer. This balancer supports queue balancing in cluster auto-scale scenarios,
/// unexpected server failure scenarios, and tries to support ideal distribution as much as possible.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LeaseBasedQueueBalancer"/> class.
/// </remarks>
/// <param name="name">The name.</param>
/// <param name="options">The options.</param>
/// <param name="leaseProvider">The lease provider.</param>
/// <param name="services">The services.</param>
/// <param name="loggerFactory">The logger factory.</param>
public partial class LeaseBasedQueueBalancer(
    string name,
    LeaseBasedQueueBalancerOptions options,
    ILeaseProvider leaseProvider,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider) : QueueBalancerBase(services, loggerFactory.CreateLogger($"{typeof(LeaseBasedQueueBalancer).FullName}.{name}")), IStreamQueueBalancer
{
    private sealed class AcquiredQueue(int order, QueueId queueId, AcquiredLease lease)
    {
        public int LeaseOrder { get; set; } = order;
        public QueueId QueueId { get; set; } = queueId;
        public AcquiredLease AcquiredLease { get; set; } = lease;
    }

    private readonly LeaseBasedQueueBalancerOptions _options = options;
    private readonly ILeaseProvider _leaseProvider = leaseProvider;
    private readonly AsyncSerialExecutor _executor = new();
    private readonly List<AcquiredQueue> _myQueues = [];
    private readonly PeriodicTimer _leaseMaintenanceTimer = new(Timeout.InfiniteTimeSpan, timeProvider);
    private readonly PeriodicTimer _leaseAcquisitionTimer = new(Timeout.InfiniteTimeSpan, timeProvider);
    private Task _leaseMaintenanceTimerTask = Task.CompletedTask;
    private Task _leaseAcquisitionTimerTask = Task.CompletedTask;
    private RoundRobinSelector<QueueId> _queueSelector;
    private int _allQueuesCount;
    private int _responsibility;
    private int _leaseOrder;

    /// <summary>
    /// Creates a new <see cref="LeaseBasedQueueBalancer"/> instance.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="name">The name.</param>
    /// <returns>The new <see cref="LeaseBasedQueueBalancer"/> instance.</returns>
    public static IStreamQueueBalancer Create(IServiceProvider services, string name)
    {
        var options = services.GetOptionsByName<LeaseBasedQueueBalancerOptions>(name);
        var leaseProvider = services.GetKeyedService<ILeaseProvider>(name)
            ?? services.GetService<ILeaseProvider>()
            ?? throw new InvalidOperationException($"No lease provider found for queue balancer '{name}'. Register an implementation of {nameof(ILeaseProvider)}.");
        return ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancer>(services, name, options, leaseProvider);
    }

    /// <inheritdoc/>
    public override async Task Initialize(IStreamQueueMapper queueMapper)
    {
        if (Cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("Cannot initialize a terminated balancer.");
        }

        ArgumentNullException.ThrowIfNull(queueMapper);
        var allQueues = queueMapper.GetAllQueues().ToList();
        _allQueuesCount = allQueues.Count;

        // Selector default to round robin selector now, but we can make a further change to make selector configurable if needed. Selector algorithm could
        // be affecting queue balancing stabilization time in cluster initializing and auto-scaling
        _queueSelector = new RoundRobinSelector<QueueId>(allQueues);
        await base.Initialize(queueMapper);
        StartMaintenanceTasks();

        void StartMaintenanceTasks()
        {
            using var _ = new ExecutionContextSuppressor();
            _leaseAcquisitionTimerTask = PeriodicallyAcquireLeasesToMeetResponsibility();
            _leaseMaintenanceTimerTask = PeriodicallyMaintainLeases();
        }
    }

    /// <inheritdoc/>
    public override async Task Shutdown()
    {
        if (Cancellation.IsCancellationRequested) return;

        // Stop acquiring and renewing leases.
        _leaseMaintenanceTimer.Dispose();
        _leaseAcquisitionTimer.Dispose();
        await Task.WhenAll(_leaseMaintenanceTimerTask, _leaseAcquisitionTimerTask);

        // Release all owned leases.
        var shutdownTask = _executor.AddNext(async () =>
        {
            _responsibility = 0;
            await ReleaseLeasesToMeetResponsibility();
        });

        // Signal shutdown.
        await base.Shutdown();
    }

    /// <inheritdoc/>
    public override IEnumerable<QueueId> GetMyQueues()
    {
        if (Cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("Cannot acquire queues from a terminated balancer.");
        }

        return _myQueues.Select(queue => queue.QueueId);
    }

    private async Task PeriodicallyMaintainLeases()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        while (await _leaseMaintenanceTimer.WaitForNextTickAsync())
        {
            try
            {
                await _executor.AddNext(MaintainLeases);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error maintaining leases.");
            }
        }

        async Task MaintainLeases()
        {
            if (Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(_myQueues.Select(queue => queue.QueueId));
            try
            {
                bool allLeasesRenewed = await RenewLeases();

                // If we lost some leases during renew after leaseAcquisitionTimer stopped, restart it.
                if (!allLeasesRenewed)
                {
                    // Make the acquisition timer fire immediately.
                    _leaseAcquisitionTimer.Period = TimeSpan.FromMilliseconds(1);
                }
            }
            finally
            {
                await NotifyOnChange(oldQueues);
            }
        }
    }

    private async Task PeriodicallyAcquireLeasesToMeetResponsibility()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        while (await _leaseAcquisitionTimer.WaitForNextTickAsync())
        {
            // Set the period for the next round.
            // It may be mutated by another method, but not concurrently.
            _leaseAcquisitionTimer.Period = _options.LeaseAcquisitionPeriod;

            try
            {
                await _executor.AddNext(AcquireLeasesToMeetResponsibility);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error acquiring leases.");
            }
        }
    }

    private async Task AcquireLeasesToMeetResponsibility()
    {
        if (Cancellation.IsCancellationRequested) return;
        var oldQueues = new HashSet<QueueId>(_myQueues.Select(queue => queue.QueueId));
        try
        {
            if (_myQueues.Count < _responsibility)
            {
                await AcquireLeasesToMeetExpectation(_responsibility, _options.LeaseLength.Divide(10));
            }
            else if (_myQueues.Count > _responsibility)
            {
                await ReleaseLeasesToMeetResponsibility();
            }
        }
        finally
        {
            await NotifyOnChange(oldQueues);
            if (_myQueues.Count == _responsibility)
            {
                // Stop the acquisition timer.
                _leaseAcquisitionTimer.Period = Timeout.InfiniteTimeSpan;
            }
        }
    }

    private async Task ReleaseLeasesToMeetResponsibility()
    {
        if (Cancellation.IsCancellationRequested) return;
        LogTraceReleaseLeasesToMeetResponsibility(Logger, _myQueues.Count, _responsibility);

        var queueCountToRelease = _myQueues.Count - _responsibility;
        if (queueCountToRelease <= 0)
        {
            return;
        }

        // Remove oldest acquired queues first, this provides max recovery time for the queues being moved.
        AcquiredLease[] queuesToGiveUp = _myQueues
            .OrderBy(queue => queue.LeaseOrder)
            .Take(queueCountToRelease)
            .Select(queue => queue.AcquiredLease)
            .ToArray();

        // Remove queues from list even if release fails, since we can let the lease expire.
        // TODO: mark for removal instead so we don't renew, and only remove leases that have not expired. - jbragg
        for (int index = _myQueues.Count - 1; index >= 0; index--)
        {
            if (queuesToGiveUp.Contains(_myQueues[index].AcquiredLease))
            {
                _myQueues.RemoveAt(index);
            }
        }

        await _leaseProvider.Release(_options.LeaseCategory, queuesToGiveUp);

        // Remove queuesToGiveUp from myQueue list after the balancer released the leases on them.
        LogDebugReleasedLeases(Logger, queueCountToRelease, _myQueues.Count, _responsibility);
    }

    private async Task AcquireLeasesToMeetExpectation(int expectedTotalLeaseCount, TimeSpan timeout)
    {
        if (Cancellation.IsCancellationRequested) return;
        LogTraceAcquireLeasesToMeetExpectation(Logger, _myQueues.Count, expectedTotalLeaseCount);

        var leasesToAcquire = expectedTotalLeaseCount - _myQueues.Count;
        if (leasesToAcquire <= 0)
        {
            return;
        }

        // tracks how many remaining possible leases there are.
        var possibleLeaseCount = _queueSelector.Count - _myQueues.Count;
        LogDebugHoldingLeased(Logger, _myQueues.Count, leasesToAcquire, expectedTotalLeaseCount, possibleLeaseCount);

        // Try to acquire leases until we have no more to acquire or no more possible
        var sw = ValueStopwatch.StartNew();
        while (!Cancellation.IsCancellationRequested && leasesToAcquire > 0 && possibleLeaseCount > 0)
        {
            // Select new queues to acquire
            List<QueueId> expectedQueues = _queueSelector.NextSelection(leasesToAcquire, _myQueues.Select(queue => queue.QueueId).ToList());

            // Build lease request from each queue
            LeaseRequest[] leaseRequests = expectedQueues
                .Select(queue => new LeaseRequest(queue.ToString(), _options.LeaseLength))
                .ToArray();

            // Add successfully acquired queue to myQueues list
            AcquireLeaseResult[] results = await _leaseProvider.Acquire(_options.LeaseCategory, leaseRequests);
            for (var i = 0; i < results.Length; i++)
            {
                AcquireLeaseResult result = results[i];
                switch (result.StatusCode)
                {
                    case ResponseCode.OK:
                        {
                            _myQueues.Add(new AcquiredQueue(_leaseOrder++, expectedQueues[i], result.AcquiredLease));
                            break;
                        }
                    case ResponseCode.TransientFailure:
                        {
                            LogWarningFailedToAcquireLeaseTransient(Logger, result.FailureException, result.AcquiredLease.ResourceKey);
                            break;
                        }
                    // This is expected much of the time.
                    case ResponseCode.LeaseNotAvailable:
                        {
                            LogDebugFailedToAcquireLeaseNotAvailable(Logger, result.FailureException, result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                    // An acquire call should not return this code, so log as error
                    case ResponseCode.InvalidToken:
                        {
                            LogErrorFailedToAcquireLeaseInvalidToken(Logger, result.FailureException, result.AcquiredLease.ResourceKey);
                            break;
                        }
                    default:
                        {
                            LogErrorUnexpectedAcquireLease(Logger, result.FailureException, result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                }
            }

            possibleLeaseCount -= expectedQueues.Count;
            leasesToAcquire = expectedTotalLeaseCount - _myQueues.Count;
            LogDebugHoldingLeased(Logger, _myQueues.Count, leasesToAcquire, expectedTotalLeaseCount, possibleLeaseCount);

            if (sw.Elapsed > timeout)
            {
                // blown our allotted time, try again next period
                break;
            }
        }

        LogDebugHoldingLeases(Logger, _myQueues.Count, _responsibility);
    }

    /// <summary>
    /// Renew leases
    /// </summary>
    /// <returns>bool - false if we failed to renew all leases</returns>
    private async Task<bool> RenewLeases()
    {
        bool allRenewed = true;
        if (Cancellation.IsCancellationRequested) return false;
        LogTraceRenewLeases(Logger, _myQueues.Count);

        if (_myQueues.Count <= 0)
        {
            return allRenewed;
        }

        var results = await _leaseProvider.Renew(_options.LeaseCategory, _myQueues.Select(queue => queue.AcquiredLease).ToArray());

        // Update myQueues list with successfully renewed leases.
        for (var i = results.Length - 1; i >= 0; i--)
        {
            AcquireLeaseResult result = results[i];
            switch (result.StatusCode)
            {
                case ResponseCode.OK:
                    {
                        _myQueues[i].AcquiredLease = result.AcquiredLease;
                        break;
                    }
                case ResponseCode.TransientFailure:
                    {
                        _myQueues.RemoveAt(i);
                        allRenewed = false;
                        LogWarningFailedToRenewLeaseTransient(Logger, result.FailureException, result.AcquiredLease.ResourceKey);
                        break;
                    }
                // These can occur if lease has expired and/or someone else has taken it.
                case ResponseCode.InvalidToken:
                case ResponseCode.LeaseNotAvailable:
                    {
                        _myQueues.RemoveAt(i);
                        allRenewed = false;
                        LogWarningFailedToRenewLeaseReason(Logger, result.FailureException, result.AcquiredLease.ResourceKey, result.StatusCode);
                        break;
                    }
                default:
                    {
                        _myQueues.RemoveAt(i);
                        allRenewed = false;
                        LogErrorUnexpectedRenewLease(Logger, result.FailureException, result.AcquiredLease.ResourceKey, result.StatusCode);
                        break;
                    }
            }
        }

        LogDebugRenewedLeases(Logger, _myQueues.Count);

        return allRenewed;
    }

    private Task NotifyOnChange(HashSet<QueueId> oldQueues)
    {
        if (Cancellation.IsCancellationRequested) return Task.CompletedTask;
        var newQueues = new HashSet<QueueId>(_myQueues.Select(queue => queue.QueueId));

        // If queue changed, notify listeners.
        return !oldQueues.SetEquals(newQueues)
            ? NotifyListeners()
            : Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
    {
        if (Cancellation.IsCancellationRequested) return;
        ScheduleUpdateResponsibilities(activeSilos).Ignore();
    }

    private async Task ScheduleUpdateResponsibilities(HashSet<SiloAddress> activeSilos)
    {
        if (Cancellation.IsCancellationRequested) return;

        try
        {
            await _executor.AddNext(() => UpdateResponsibilities(activeSilos));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating lease responsibilities.");
        }
    }

    /// <summary>
    /// Checks to see if this balancer should be greedy, which means it attempts to grab one
    ///   more queue than the non-greedy balancers.
    /// </summary>
    /// <param name="overflow">number of free queues, assuming all balancers meet their minimum responsibilities</param>
    /// <param name="activeSilos">number of active silos hosting queues</param>
    /// <returns>bool - true indicates that the balancer should try to acquire one
    ///   more queue than the non-greedy balancers</returns>
    private bool ShouldBeGreedy(int overflow, HashSet<SiloAddress> activeSilos)
    {
        // If using multiple stream providers, this will select the same silos to be greedy for
        //   all providers, aggravating imbalance as stream provider count increases.
        return activeSilos.OrderBy(silo => silo)
                          .Take(overflow)
                          .Contains(SiloAddress);
    }

    private async Task UpdateResponsibilities(HashSet<SiloAddress> activeSilos)
    {
        if (Cancellation.IsCancellationRequested) return;
        var activeSiloCount = Math.Max(1, activeSilos.Count);
        _responsibility = _allQueuesCount / activeSiloCount;
        var overflow = _allQueuesCount % activeSiloCount;
        if (overflow != 0 && ShouldBeGreedy(overflow, activeSilos))
        {
            _responsibility++;
        }

        LogDebugUpdatingResponsibilities(Logger, _allQueuesCount, activeSiloCount, _responsibility, _myQueues.Count);

        if (_myQueues.Count < _responsibility && _leaseAcquisitionTimer.Period == Timeout.InfiniteTimeSpan)
        {
            // Ensure the acquisition timer is running.
            _leaseAcquisitionTimer.Period = _options.LeaseAcquisitionPeriod;
        }

        _leaseMaintenanceTimer.Period = _options.LeaseRenewPeriod;
        await AcquireLeasesToMeetResponsibility();
    }

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "ReleaseLeasesToMeetResponsibility. QueueCount: {QueueCount}, Responsibility: {Responsibility}"
    )]
    private static partial void LogTraceReleaseLeasesToMeetResponsibility(ILogger logger, int queueCount, int responsibility);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Released leases for {QueueCount} queues. Holding leases for {HoldingQueueCount} of an expected {MinQueueCount} queues."
    )]
    private static partial void LogDebugReleasedLeases(ILogger logger, int queueCount, int holdingQueueCount, int minQueueCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "AcquireLeasesToMeetExpectation. QueueCount: {QueueCount}, ExpectedTotalLeaseCount: {ExpectedTotalLeaseCount}"
    )]
    private static partial void LogTraceAcquireLeasesToMeetExpectation(ILogger logger, int queueCount, int expectedTotalLeaseCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Holding leased for {QueueCount} queues. Trying to acquire {acquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount}"
    )]
    private static partial void LogDebugHoldingLeased(ILogger logger, int queueCount, int acquireQueueCount, int targetQueueCount, int possibleLeaseCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to acquire lease {LeaseKey} due to transient error."
    )]
    private static partial void LogWarningFailedToAcquireLeaseTransient(ILogger logger, Exception exception, string leaseKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to acquire lease {LeaseKey} due to {Reason}."
    )]
    private static partial void LogDebugFailedToAcquireLeaseNotAvailable(ILogger logger, Exception exception, string leaseKey, ResponseCode reason);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to acquire acquire {LeaseKey} unexpected invalid token."
    )]
    private static partial void LogErrorFailedToAcquireLeaseInvalidToken(ILogger logger, Exception exception, string leaseKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unexpected response to acquire request of lease {LeaseKey}. StatusCode {StatusCode}."
    )]
    private static partial void LogErrorUnexpectedAcquireLease(ILogger logger, Exception exception, string leaseKey, ResponseCode statusCode);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Holding leased for {QueueCount} queues. Trying to acquire {acquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount} lease"
    )]
    private static partial void LogDebugHoldingLeasedAgain(ILogger logger, int queueCount, int acquireQueueCount, int targetQueueCount, int possibleLeaseCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Holding leases for {QueueCount} of an expected {MinQueueCount} queues."
    )]
    private static partial void LogDebugHoldingLeases(ILogger logger, int queueCount, int minQueueCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "RenewLeases. QueueCount: {QueueCount}"
    )]
    private static partial void LogTraceRenewLeases(ILogger logger, int queueCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to renew lease {LeaseKey} due to transient error."
    )]
    private static partial void LogWarningFailedToRenewLeaseTransient(ILogger logger, Exception exception, string leaseKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to renew lease {LeaseKey} due to {Reason}."
    )]
    private static partial void LogWarningFailedToRenewLeaseReason(ILogger logger, Exception exception, string leaseKey, ResponseCode reason);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unexpected response to renew of lease {LeaseKey}. StatusCode {StatusCode}."
    )]
    private static partial void LogErrorUnexpectedRenewLease(ILogger logger, Exception exception, string leaseKey, ResponseCode statusCode);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Renewed leases for {QueueCount} queues."
    )]
    private static partial void LogDebugRenewedLeases(ILogger logger, int queueCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Updating Responsibilities for {QueueCount} queue over {SiloCount} silos. Need {MinQueueCount} queues, have {MyQueueCount}"
    )]
    private static partial void LogDebugUpdatingResponsibilities(ILogger logger, int queueCount, int siloCount, int minQueueCount, int myQueueCount);
}
