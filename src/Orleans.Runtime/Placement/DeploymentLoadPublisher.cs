using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Dissemination;
using Orleans.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// This class collects runtime statistics for all silos in the current deployment for use by placement.
    /// </summary>
    internal sealed partial class DeploymentLoadPublisher : SystemTarget, IDeploymentLoadPublisher, ISiloStatusListener, ILifecycleParticipant<ISiloLifecycle>
    {
        private const int MaxRetainedTerminatedSiloEndpoints = 4096;
        private readonly ILocalSiloDetails _siloDetails;
        private readonly ISiloStatusOracle _siloStatusOracle;
        private readonly IInternalGrainFactory _grainFactory;
        private readonly ActivationDirectory _activationDirectory;
        private readonly IActivationWorkingSet _activationWorkingSet;
        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly IOptions<LoadSheddingOptions> _loadSheddingOptions;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> _periodicStats;
        private readonly object _statisticsUpdateLock = new();
        private readonly Queue<SiloAddress> _statisticsNotificationOrder = new();
        private readonly Dictionary<SiloAddress, StatisticsNotification> _pendingStatisticsNotifications = new();
        private readonly Dictionary<IPEndPoint, int> _terminatedSiloGenerations = new();
        private readonly LinkedList<IPEndPoint> _terminatedSiloGenerationOrder = new();
        private readonly Dictionary<IPEndPoint, LinkedListNode<IPEndPoint>> _terminatedSiloGenerationNodes = new();
        private readonly TimeSpan _statisticsRefreshTime;
        private readonly List<ISiloStatisticsChangeListener> _siloStatisticsChangeListeners;
        private readonly ILogger _logger;

        private long _lastUpdateDateTimeTicks;
        private IDisposable? _publishTimer;
        private bool _isDrainingStatisticsNotifications;

        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics => _periodicStats;

        public SiloRuntimeStatistics LocalRuntimeStatistics { get; private set; } = null!;

        public DeploymentLoadPublisher(
            ILocalSiloDetails siloDetails,
            ISiloStatusOracle siloStatusOracle,
            IOptions<DeploymentLoadPublisherOptions> options,
            IInternalGrainFactory grainFactory,
            ILoggerFactory loggerFactory,
            ActivationDirectory activationDirectory,
            IActivationWorkingSet activationWorkingSet,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            IServiceProvider serviceProvider,
            SystemTargetShared shared)
            : base(Constants.DeploymentLoadPublisherSystemTargetType, shared)
        {
            _logger = loggerFactory.CreateLogger<DeploymentLoadPublisher>();
            _siloDetails = siloDetails;
            _siloStatusOracle = siloStatusOracle;
            _grainFactory = grainFactory;
            _activationDirectory = activationDirectory;
            _activationWorkingSet = activationWorkingSet;
            _environmentStatisticsProvider = environmentStatisticsProvider;
            _loadSheddingOptions = loadSheddingOptions;
            _serviceProvider = serviceProvider;
            _statisticsRefreshTime = options.Value.DeploymentLoadPublisherRefreshTime;
            _periodicStats = new ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>();
            _siloStatisticsChangeListeners = new List<ISiloStatisticsChangeListener>();
            siloStatusOracle.SubscribeToSiloStatusEvents(this);
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            LogDebugStartingDeploymentLoadPublisher(_logger);

            if (_statisticsRefreshTime > TimeSpan.Zero)
            {
                // Randomize PublishStatistics timer,
                // but also upon start publish my stats to everyone and take everyone's stats for me to start with something.
                var randomTimerOffset = RandomTimeSpan.Next(_statisticsRefreshTime);
                _publishTimer = RegisterTimer(
                    static state => ((DeploymentLoadPublisher)state!).PublishStatistics(CancellationToken.None),
                    this,
                    randomTimerOffset,
                    _statisticsRefreshTime);
            }

            await RefreshClusterStatistics(cancellationToken);
            await PublishStatistics(cancellationToken);
            LogDebugStartedDeploymentLoadPublisher(_logger);
        }

<<<<<<< HEAD
        private async Task PublishStatistics(CancellationToken cancellationToken)
||||||| parent of f658f01d66 (feat(runtime): complete efficient broadcast behaviors)
        private async Task PublishStatistics()
=======
        internal async Task PublishStatistics()
>>>>>>> f658f01d66 (feat(runtime): complete efficient broadcast behaviors)
        {
            try
            {
                LogTracePublishStatistics(_logger);

                // Ensure that our timestamp is monotonically increasing.
                var ticks = _lastUpdateDateTimeTicks = Math.Max(_lastUpdateDateTimeTicks + 1, DateTime.UtcNow.Ticks);

                var myStats = new SiloRuntimeStatistics(
                    _activationDirectory.Count,
                    _activationWorkingSet.Count,
                    _environmentStatisticsProvider,
                    _loadSheddingOptions,
                    new DateTime(ticks, DateTimeKind.Utc));

                // Update statistics locally.
                LocalRuntimeStatistics = myStats;
                UpdateRuntimeStatisticsInternal(_siloDetails.SiloAddress, myStats);
                DeploymentLoadPublisherEvents.EmitPublished(_siloDetails.SiloAddress, myStats);

                // Inform other cluster members about our refreshed statistics.
                var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToArray();
                IReadOnlyCollection<SiloAddress> directRecipients = members;
                if (await TryPublishStatisticsViaDissemination(myStats))
                {
<<<<<<< HEAD
                    await PublishStatisticsDirectly(myStats, members, cancellationToken);
||||||| parent of f658f01d66 (feat(runtime): complete efficient broadcast behaviors)
                    await PublishStatisticsDirectly(myStats, members);
=======
                    try
                    {
                        var dissemination = _serviceProvider.GetService<IDisseminationService>();
                        var disseminationNamespace = _serviceProvider.GetService<DeploymentLoadStatisticsDisseminationNamespace>();
                        if (dissemination is not null && disseminationNamespace is not null)
                        {
                            var unconfirmedPeers = dissemination.GetUnconfirmedPeers(disseminationNamespace).ToHashSet();
                            directRecipients = members.Where(unconfirmedPeers.Contains).ToArray();
                        }
                    }
                    catch (Exception exception)
                    {
                        LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                    }
>>>>>>> f658f01d66 (feat(runtime): complete efficient broadcast behaviors)
                }

                await PublishStatisticsDirectly(myStats, directRecipients);
                DeploymentLoadPublisherEvents.EmitClusterRefreshed(_siloDetails.SiloAddress, _periodicStats);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (Exception exc)
            {
                LogWarningRuntimeStatisticsUpdateFailure2(_logger, exc);
            }
        }

        public Task UpdateRuntimeStatistics(
            SiloAddress siloAddress,
            SiloRuntimeStatistics siloStats,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateRuntimeStatisticsInternal(siloAddress, siloStats);
            return Task.CompletedTask;
        }

        internal DisseminationApplyResult ApplyDisseminatedRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats) =>
            UpdateRuntimeStatisticsInternal(siloAddress, siloStats);

        internal bool IsRuntimeStatisticsObsolete(SiloAddress siloAddress, long timestampTicks)
        {
            lock (_statisticsUpdateLock)
            {
                if (IsTerminatedGenerationUnsafe(siloAddress)
                    || _siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
                {
                    return true;
                }

                return _periodicStats.TryGetValue(siloAddress, out var old) && old.DateTime.Ticks > timestampTicks;
            }
        }

        internal IReadOnlyCollection<SiloAddress> GetActiveSilosForStatisticsDigest() =>
            _siloStatusOracle.GetApproximateSiloStatuses(onlyActive: true).Keys;

        internal int RetainedTerminatedSiloEndpointCount
        {
            get
            {
                lock (_statisticsUpdateLock)
                {
                    return _terminatedSiloGenerations.Count;
                }
            }
        }

        internal int RetainedTerminatedSiloOrderCount
        {
            get
            {
                lock (_statisticsUpdateLock)
                {
                    return _terminatedSiloGenerationOrder.Count;
                }
            }
        }

        internal async Task<bool> TryPublishStatisticsViaDissemination(SiloRuntimeStatistics myStats)
        {
            var timeProvider = _serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            using var cancellation = new CancellationTokenSource(_statisticsRefreshTime, timeProvider);
            try
            {
                var dissemination = _serviceProvider.GetService<IDisseminationService>();
                var disseminationNamespace = _serviceProvider.GetService<DeploymentLoadStatisticsDisseminationNamespace>();
                if (dissemination is null || disseminationNamespace is null || !disseminationNamespace.Options.Enabled)
                {
                    return false;
                }

                return await dissemination.Publish(
                        disseminationNamespace,
                        _siloDetails.SiloAddress,
                        myStats.DateTime.Ticks,
                        cancellation.Token)
                    .AsTask()
                    .WaitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                LogDebugRuntimeStatisticsDisseminationTimedOut(_logger, _statisticsRefreshTime);
                return false;
            }
            catch (Exception exception)
            {
                LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                return false;
            }
        }

        private async Task PublishStatisticsDirectly(
            SiloRuntimeStatistics myStats,
            IReadOnlyCollection<SiloAddress> members,
            CancellationToken cancellationToken)
        {
            var tasks = new List<Task>(members.Count);
            foreach (var siloAddress in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // No need to make a grain call to ourselves.
                if (siloAddress.Equals(_siloDetails.SiloAddress))
                {
                    continue;
                }

                try
                {
                    var deploymentLoadPublisher = _grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(Constants.DeploymentLoadPublisherSystemTargetType, siloAddress);
                    tasks.Add(deploymentLoadPublisher.UpdateRuntimeStatistics(_siloDetails.SiloAddress, myStats, cancellationToken));
                }
                catch (Exception exception)
                {
                    LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                }
            }

            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }

        private DisseminationApplyResult UpdateRuntimeStatisticsInternal(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            LogTraceUpdateRuntimeStatistics(_logger, siloAddress);
            bool drainNotifications;
            lock (_statisticsUpdateLock)
            {
                if (IsTerminatedGenerationUnsafe(siloAddress)
                    || _siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
                {
                    return DisseminationApplyResult.Rejected;
                }

                ClearOlderTerminatedGenerationUnsafe(siloAddress);

                if (_periodicStats.TryGetValue(siloAddress, out var old))
                {
                    if (old.DateTime > siloStats.DateTime)
                    {
                        return DisseminationApplyResult.Obsolete;
                    }

                    if (old.DateTime == siloStats.DateTime)
                    {
                        return DisseminationApplyResult.Duplicate;
                    }
                }

                _periodicStats[siloAddress] = siloStats;
                drainNotifications = EnqueueStatisticsNotificationUnsafe(new(siloAddress, siloStats));
            }

            if (drainNotifications)
            {
                DrainStatisticsNotifications();
            }

            return DisseminationApplyResult.Applied;
        }

        internal async Task RefreshClusterStatistics(CancellationToken cancellationToken = default)
        {
            LogTraceRefreshStatistics(_logger);
            await this.RunOrQueueTask(() =>
                {
                    var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                    var tasks = new List<Task>(members.Count);
                    foreach (var siloAddress in members)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        tasks.Add(RefreshSiloStatistics(siloAddress, cancellationToken));
                    }

                    return Task.WhenAll(tasks).WaitAsync(cancellationToken);
                });
        }

        private async Task RefreshSiloStatistics(SiloAddress silo, CancellationToken cancellationToken)
        {
            try
            {
                var statistics = await _grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo)
                    .GetRuntimeStatistics(cancellationToken);
                UpdateRuntimeStatisticsInternal(silo, statistics);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogWarningRuntimeStatisticsUpdateFailure3(_logger, exception, silo);
            }
        }

        public bool SubscribeToStatisticsChangeEvents(ISiloStatisticsChangeListener observer)
        {
            lock (_siloStatisticsChangeListeners)
            {
                if (_siloStatisticsChangeListeners.Contains(observer)) return false;

                _siloStatisticsChangeListeners.Add(observer);
                return true;
            }
        }

        public bool UnsubscribeStatisticsChangeEvents(ISiloStatisticsChangeListener observer)
        {
            lock (_siloStatisticsChangeListeners)
            {
                return _siloStatisticsChangeListeners.Remove(observer);
            }
        }

        private ExceptionDispatchInfo? NotifyAllStatisticsChangeEventsSubscribers(SiloAddress silo, SiloRuntimeStatistics? stats)
        {
            ISiloStatisticsChangeListener[] subscribers;
            lock (_siloStatisticsChangeListeners)
            {
                subscribers = [.. _siloStatisticsChangeListeners];
            }

            ExceptionDispatchInfo? failure = null;
            foreach (var subscriber in subscribers)
            {
                try
                {
                    if (stats == null)
                    {
                        subscriber.RemoveSilo(silo);
                    }
                    else
                    {
                        subscriber.SiloStatisticsChangeNotification(silo, stats);
                    }
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            return failure;
        }

        private bool EnqueueStatisticsNotificationUnsafe(StatisticsNotification notification)
        {
            if (!_pendingStatisticsNotifications.ContainsKey(notification.SiloAddress))
            {
                _statisticsNotificationOrder.Enqueue(notification.SiloAddress);
            }

            _pendingStatisticsNotifications[notification.SiloAddress] = notification;
            if (_isDrainingStatisticsNotifications)
            {
                return false;
            }

            _isDrainingStatisticsNotifications = true;
            return true;
        }

        private void DrainStatisticsNotifications()
        {
            ExceptionDispatchInfo? failure = null;
            while (true)
            {
                StatisticsNotification notification;
                bool hasNotification;
                lock (_statisticsUpdateLock)
                {
                    if (!_statisticsNotificationOrder.TryDequeue(out var siloAddress))
                    {
                        _isDrainingStatisticsNotifications = false;
                        hasNotification = false;
                        notification = default;
                    }
                    else
                    {
                        hasNotification = _pendingStatisticsNotifications.Remove(siloAddress, out notification);
                    }
                }

                if (!hasNotification)
                {
                    failure?.Throw();
                    return;
                }

                if (notification.Statistics is { } statistics)
                {
                    var notificationFailure = NotifyAllStatisticsChangeEventsSubscribers(notification.SiloAddress, statistics);
                    failure ??= notificationFailure;
                    try
                    {
                        DeploymentLoadPublisherEvents.EmitReceived(
                            notification.SiloAddress,
                            _siloDetails.SiloAddress,
                            statistics);
                    }
                    catch (Exception exception)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(exception);
                    }
                }
                else
                {
                    try
                    {
                        DeploymentLoadPublisherEvents.EmitRemoved(notification.SiloAddress, _siloDetails.SiloAddress);
                    }
                    catch (Exception exception)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(exception);
                    }

                    var notificationFailure = NotifyAllStatisticsChangeEventsSubscribers(notification.SiloAddress, null);
                    failure ??= notificationFailure;
                }
            }
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            WorkItemGroup.QueueAction(() =>
            {
                Utils.SafeExecute(() => OnSiloStatusChange(updatedSilo, status), _logger);
            });
        }

        internal void OnSiloStatusChange(SiloAddress updatedSilo, SiloStatus status)
        {
            if (!status.IsTerminating()) return;

            bool drainNotifications;
            lock (_statisticsUpdateLock)
            {
                var hasNewerTombstone = _terminatedSiloGenerations.TryGetValue(updatedSilo.Endpoint, out var generation)
                    && generation >= updatedSilo.Generation;
                if (!hasNewerTombstone)
                {
                    _terminatedSiloGenerations[updatedSilo.Endpoint] = updatedSilo.Generation;
                    if (_terminatedSiloGenerationNodes.Remove(updatedSilo.Endpoint, out var existingNode))
                    {
                        _terminatedSiloGenerationOrder.Remove(existingNode);
                    }

                    _terminatedSiloGenerationNodes[updatedSilo.Endpoint] =
                        _terminatedSiloGenerationOrder.AddLast(updatedSilo.Endpoint);
                    TrimTerminatedSiloGenerationsUnsafe();
                }

                var removed = _periodicStats.TryRemove(updatedSilo, out _);
                if (!removed && !_pendingStatisticsNotifications.ContainsKey(updatedSilo))
                {
                    return;
                }

                drainNotifications = EnqueueStatisticsNotificationUnsafe(new(updatedSilo, Statistics: null));
            }

            if (drainNotifications)
            {
                DrainStatisticsNotifications();
            }
        }

        private readonly record struct StatisticsNotification(
            SiloAddress SiloAddress,
            SiloRuntimeStatistics? Statistics);

        private bool IsTerminatedGenerationUnsafe(SiloAddress siloAddress) =>
            _terminatedSiloGenerations.TryGetValue(siloAddress.Endpoint, out var generation)
            && generation >= siloAddress.Generation;

        private void ClearOlderTerminatedGenerationUnsafe(SiloAddress siloAddress)
        {
            if (_terminatedSiloGenerations.TryGetValue(siloAddress.Endpoint, out var generation)
                && generation < siloAddress.Generation)
            {
                _terminatedSiloGenerations.Remove(siloAddress.Endpoint);
                if (_terminatedSiloGenerationNodes.Remove(siloAddress.Endpoint, out var node))
                {
                    _terminatedSiloGenerationOrder.Remove(node);
                }
            }
        }

        private void TrimTerminatedSiloGenerationsUnsafe()
        {
            while (_terminatedSiloGenerations.Count > MaxRetainedTerminatedSiloEndpoints
                && _terminatedSiloGenerationOrder.First is { } candidate)
            {
                _terminatedSiloGenerationOrder.RemoveFirst();
                _terminatedSiloGenerationNodes.Remove(candidate.Value);
                _terminatedSiloGenerations.Remove(candidate.Value);
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(
                nameof(DeploymentLoadPublisher),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartAsync,
                DisposePublishTimer);

            Task DisposePublishTimer(CancellationToken ct)
            {
                _publishTimer!.Dispose(); // Preserve the existing lifecycle contract that publishing is enabled.
                return Task.CompletedTask;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting DeploymentLoadPublisher"
        )]
        private static partial void LogDebugStartingDeploymentLoadPublisher(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Started DeploymentLoadPublisher"
        )]
        private static partial void LogDebugStartedDeploymentLoadPublisher(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "PublishStatistics"
        )]
        private static partial void LogTracePublishStatistics(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_1,
            Level = LogLevel.Warning,
            Message = "An unexpected exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignored"
        )]
        private static partial void LogWarningRuntimeStatisticsUpdateFailure1(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Runtime statistics dissemination exceeded {Timeout}. Direct publication continues delivery."
        )]
        private static partial void LogDebugRuntimeStatisticsDisseminationTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(
            EventId = (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_2,
            Level = LogLevel.Warning,
            Message = "An exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignoring"
        )]
        private static partial void LogWarningRuntimeStatisticsUpdateFailure2(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "UpdateRuntimeStatistics from {Server}"
        )]
        private static partial void LogTraceUpdateRuntimeStatistics(ILogger logger, SiloAddress server);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "RefreshStatistics"
        )]
        private static partial void LogTraceRefreshStatistics(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_3,
            Level = LogLevel.Warning,
            Message = "An unexpected exception was thrown from RefreshStatistics by ISiloControl.GetRuntimeStatistics({SiloAddress}). Will keep using stale statistics."
        )]
        private static partial void LogWarningRuntimeStatisticsUpdateFailure3(ILogger logger, Exception exception, SiloAddress siloAddress);
    }
}
