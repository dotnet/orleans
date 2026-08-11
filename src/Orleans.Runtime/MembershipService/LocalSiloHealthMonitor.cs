
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.MembershipService
{
    [Flags]
    internal enum LocalSiloHealthCheckCategory
    {
        None = 0,
        Local = 1,
        Network = 2,
        All = Local | Network,
    }

    internal enum LocalSiloHealthCheckKind
    {
        MembershipStatus,
        SiloSuspected,
        HealthCheckParticipant,
        ThreadPoolQueueDelay,
        ProbeRequests,
        ProbeResponses,
        GarbageCollectionPause,
        RuntimeStall,
        ComponentHealthCheckStall,
    }

    internal readonly record struct LocalSiloHealthEvent(
        DateTimeOffset Timestamp,
        LocalSiloHealthCheckKind Kind,
        LocalSiloHealthCheckCategory Category,
        string? Source,
        int Score,
        string? Complaint,
        TimeSpan? Duration);

    internal readonly record struct LocalSiloHealthStatus(int Score, ImmutableArray<LocalSiloHealthEvent> Events)
    {
        public ImmutableArray<string> Complaints
            => [.. Events.Where(static status => status.Complaint is not null).Select(static status => status.Complaint!)];
    }

    internal interface ILocalSiloHealthMonitor
    {
        /// <summary>
        /// Returns the aggregate local health status over the provided period.
        /// </summary>
        /// <param name="period">The period ending at the current time to aggregate.</param>
        /// <param name="categories">The categories of health checks to include.</param>
        /// <returns>The aggregate health status.</returns>
        LocalSiloHealthStatus GetLocalHealthStatus(TimeSpan period, LocalSiloHealthCheckCategory categories);

        /// <summary>
        /// The most recent list of detected health issues.
        /// </summary>
        ImmutableArray<string> Complaints { get; }
    }

    internal interface ILocalSiloHealthEventRecorder
    {
        void RecordHealthEvent(
            LocalSiloHealthCheckKind kind,
            int score,
            string? complaint,
            TimeSpan? duration = null,
            string? source = null);
    }

    /// <summary>
    /// Monitors the health of the local node using a combination of heuristics to create a health degradation score which
    /// is exposed as a boolean value: whether or not the local node's health is degraded.
    /// </summary>
    /// <remarks>
    /// The primary goal of this functionality is to passify degraded nodes so that they do not evict healthy nodes.
    /// This functionality is inspired by the Lifeguard paper (https://arxiv.org/abs/1707.00788), which is a set of extensions
    /// to the SWIM membership algorithm (https://research.cs.cornell.edu/projects/Quicksilver/public_pdfs/SWIM.pdf). Orleans
    /// uses a strong consistency membership algorithm, and not all of the Lifeguard extensions to SWIM apply to Orleans'
    /// membership algorithm (refutation, for example).
    /// The monitor implements the following heuristics:
    /// <list type="bullet">
    ///   <item><description>Check that this silos is marked as active in membership.</description></item>
    ///   <item><description>Check that no other silo suspects this silo.</description></item>
    ///   <item><description>Check for recently received successful ping responses (via <see cref="IProbeHealthMonitor"/>).</description></item>
    ///   <item><description>Check for recently received ping requests (via <see cref="IProbeHealthMonitor"/>).</description></item>
    ///   <item><description>Check that the .NET Thread Pool is able to process work items within one second.</description></item>
    ///   <item><description>Check that local async timers have been firing on-time (within 3 seconds of their due time).</description></item>
    /// </list>
    /// </remarks>
    internal partial class LocalSiloHealthMonitor :
        ILifecycleParticipant<ISiloLifecycle>,
        ILifecycleObserver,
        ILocalSiloHealthMonitor,
        ILocalSiloHealthEventRecorder
    {
        internal const int MaxScore = 8;
        private static readonly TimeSpan HistoryDuration = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MinimumCheckPeriod = TimeSpan.FromSeconds(1);
        private readonly List<IHealthCheckParticipant> _healthCheckParticipants;
        private readonly List<LocalSiloHealthEvent> _healthEvents = [];
        private readonly IMembershipManager _membershipManager;
        private readonly IProbeHealthMonitor _probeHealthMonitor;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ILogger<LocalSiloHealthMonitor> _log;
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IAsyncTimer _degradationCheckTimer;
        private readonly ThreadPoolMonitor _threadPoolMonitor;
        private readonly TimeProvider _timeProvider;
#if NET9_0_OR_GREATER
        private readonly Lock _historyLock = new();
        private readonly Lock _samplingLock = new();
#else
        private readonly object _historyLock = new();
        private readonly object _samplingLock = new();
#endif
        private Task? _runTask;
        private bool _isActive;
        private DateTime _lastHealthCheckTime;
        private long? _clusteredSinceTimestamp;
        private long? _lastHealthCheckTimestamp;
        private LocalSiloHealthStatus _latestStatus;

        public LocalSiloHealthMonitor(
            IEnumerable<IHealthCheckParticipant> healthCheckParticipants,
            IMembershipManager membershipManager,
            IProbeHealthMonitor probeHealthMonitor,
            ILocalSiloDetails localSiloDetails,
            ILogger<LocalSiloHealthMonitor> log,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IAsyncTimerFactory timerFactory,
            ILoggerFactory loggerFactory,
            [FromKeyedServices(TimeProviderNames.Membership)] TimeProvider timeProvider)
        {
            _healthCheckParticipants = healthCheckParticipants.ToList();
            _membershipManager = membershipManager;
            _probeHealthMonitor = probeHealthMonitor;
            _localSiloDetails = localSiloDetails;
            _log = log;
            _clusterMembershipOptions = clusterMembershipOptions.Value;
            _timeProvider = timeProvider;
            _degradationCheckTimer = timerFactory.Create(
                _clusterMembershipOptions.LocalHealthDegradationMonitoringPeriod,
                nameof(LocalSiloHealthMonitor),
                timeProvider);
            _threadPoolMonitor = new ThreadPoolMonitor(loggerFactory.CreateLogger<ThreadPoolMonitor>(), timeProvider);
        }

        /// <inheritdoc />
        public ImmutableArray<string> Complaints { get; private set; } = [];

        /// <inheritdoc />
        /// <remarks>Periods longer than the one-minute retention window are limited to the retained history.</remarks>
        public LocalSiloHealthStatus GetLocalHealthStatus(TimeSpan period, LocalSiloHealthCheckCategory categories)
        {
            if (period < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(period), period, "The aggregation period must not be negative.");
            }

            DateTimeOffset now;
            lock (_samplingLock)
            {
                EnsureHealthCheck(_timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
                now = _timeProvider.GetUtcNow();
            }

            lock (_historyLock)
            {
                RemoveExpiredEvents(now);
                var lookback = period > HistoryDuration ? HistoryDuration : period;
                return AggregateHealthStatus(now - lookback, now, categories);
            }
        }

        void ILocalSiloHealthEventRecorder.RecordHealthEvent(
            LocalSiloHealthCheckKind kind,
            int score,
            string? complaint,
            TimeSpan? duration,
            string? source)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(score);
            var now = _timeProvider.GetUtcNow();
            lock (_historyLock)
            {
                _healthEvents.Add(new(now, kind, GetCategory(kind), source, score, complaint, duration));
                RemoveExpiredEvents(now);
            }
        }

        private LocalSiloHealthStatus EnsureHealthCheck(DateTimeOffset now, long timestamp)
        {
            if (_lastHealthCheckTimestamp is { } lastCheck
                && _timeProvider.GetElapsedTime(lastCheck, timestamp) < MinimumCheckPeriod)
            {
                return _latestStatus;
            }

            var events = new List<LocalSiloHealthEvent>(_healthCheckParticipants.Count + 6);
            var complaints = new List<string>();
            CheckMembershipStatus(now.UtcDateTime, events, complaints);
            CheckLocalHealthCheckParticipants(now, events, complaints);
            CheckThreadPoolQueueDelay(now, events, complaints);

            if (_isActive)
            {
                var membershipSnapshot = _membershipManager.CurrentSnapshot;
                if (membershipSnapshot.ActiveNodeCount <= 1)
                {
                    _clusteredSinceTimestamp = null;
                }
                else
                {
                    _clusteredSinceTimestamp ??= timestamp;
                }

                // Only consider certain checks if the silo has been a member of a multi-silo cluster for a certain period.
                var recencyWindow = _clusterMembershipOptions.ProbeTimeout.Multiply(_clusterMembershipOptions.NumMissedProbesLimit);
                if (_clusteredSinceTimestamp is { } clusteredSince
                    && _timeProvider.GetElapsedTime(clusteredSince, timestamp) > recencyWindow)
                {
                    CheckReceivedProbeResponses(now, events, complaints);
                    CheckReceivedProbeRequests(now, events, complaints);
                }
            }

            _lastHealthCheckTimestamp = _timeProvider.GetTimestamp();
            lock (_historyLock)
            {
                _healthEvents.AddRange(events);
                RemoveExpiredEvents(now);
            }

            var score = Math.Clamp(events.Sum(static status => status.Score), 0, MaxScore);
            Complaints = [.. complaints];
            return _latestStatus = new(score, [.. events]);
        }

        private void CheckThreadPoolQueueDelay(
            DateTimeOffset now,
            List<LocalSiloHealthEvent> events,
            List<string> complaints)
        {
            var delay = _threadPoolMonitor.MeasureQueueDelay();
            var score = (int)delay.TotalSeconds;
            string? complaint = null;
            if (score >= 1)
            {
                var logLevel = score >= 10 ? LogLevel.Error : LogLevel.Warning;
                LogThreadPoolDelay(logLevel, delay.TotalSeconds);
                complaint = $".NET Thread Pool is exhibiting delays of {delay.TotalSeconds}s. This can indicate .NET Thread Pool starvation, very long .NET GC pauses, or other runtime or machine pauses.";
                complaints.Add(complaint);
            }

            events.Add(new(
                now,
                LocalSiloHealthCheckKind.ThreadPoolQueueDelay,
                GetCategory(LocalSiloHealthCheckKind.ThreadPoolQueueDelay),
                Source: null,
                score,
                complaint,
                delay));
        }

        private void CheckMembershipStatus(
            DateTime now,
            List<LocalSiloHealthEvent> events,
            List<string> complaints)
        {
            var membershipSnapshot = _membershipManager.CurrentSnapshot;
            if (membershipSnapshot.Entries.TryGetValue(_localSiloDetails.SiloAddress, out var membershipEntry))
            {
                var statusScore = 0;
                string? statusComplaint = null;
                if (membershipEntry.Status != SiloStatus.Active)
                {
                    LogSiloNotActive(membershipEntry.Status);
                    statusComplaint = $"This silo is not active (Status: {membershipEntry.Status}) and is therefore not healthy.";
                    complaints.Add(statusComplaint);
                    statusScore = MaxScore;
                }

                events.Add(new(
                    now,
                    LocalSiloHealthCheckKind.MembershipStatus,
                    GetCategory(LocalSiloHealthCheckKind.MembershipStatus),
                    Source: null,
                    statusScore,
                    statusComplaint,
                    Duration: null));

                // Check if there are valid votes against this node.
                var expiration = _clusterMembershipOptions.DeathVoteExpirationTimeout;
                var freshVotes = membershipEntry.GetFreshVotes(now, expiration);
                foreach (var vote in freshVotes)
                {
                    if (membershipSnapshot.GetSiloStatus(vote.Item1) == SiloStatus.Active)
                    {
                        LogSiloSuspected(vote.Item1, vote.Item2);
                        var complaint = $"Silo {vote.Item1} recently suspected this silo is dead at {vote.Item2}.";
                        complaints.Add(complaint);
                        events.Add(new(
                            now,
                            LocalSiloHealthCheckKind.SiloSuspected,
                            GetCategory(LocalSiloHealthCheckKind.SiloSuspected),
                            vote.Item1.ToParsableString(),
                            Score: 1,
                            complaint,
                            Duration: null));
                    }
                }
            }
            else
            {
                LogMembershipEntryNotFound();
                const string Complaint = "Could not find a membership entry for this silo";
                complaints.Add(Complaint);
                events.Add(new(
                    now,
                    LocalSiloHealthCheckKind.MembershipStatus,
                    GetCategory(LocalSiloHealthCheckKind.MembershipStatus),
                    Source: null,
                    Score: MaxScore,
                    Complaint,
                    Duration: null));
            }
        }

        private void CheckReceivedProbeRequests(
            DateTimeOffset now,
            List<LocalSiloHealthEvent> events,
            List<string> complaints)
        {
            var membershipSnapshot = _membershipManager.CurrentSnapshot;
            var score = _probeHealthMonitor.CheckReceivedProbeRequests(now.UtcDateTime, membershipSnapshot.ActiveNodeCount, out var complaint);
            if (complaint is not null)
            {
                complaints.Add(complaint);
            }

            events.Add(new(
                now,
                LocalSiloHealthCheckKind.ProbeRequests,
                GetCategory(LocalSiloHealthCheckKind.ProbeRequests),
                Source: null,
                score,
                complaint,
                Duration: null));
        }

        private void CheckReceivedProbeResponses(
            DateTimeOffset now,
            List<LocalSiloHealthEvent> events,
            List<string> complaints)
        {
            var membershipSnapshot = _membershipManager.CurrentSnapshot;
            // Use ActiveNodeCount - 1 as a proxy for monitored node count (we don't monitor ourselves)
            var monitoredNodeCount = Math.Max(0, membershipSnapshot.ActiveNodeCount - 1);
            var score = _probeHealthMonitor.CheckReceivedProbeResponses(now.UtcDateTime, monitoredNodeCount, out var complaint);
            if (complaint is not null)
            {
                complaints.Add(complaint);
            }

            events.Add(new(
                now,
                LocalSiloHealthCheckKind.ProbeResponses,
                GetCategory(LocalSiloHealthCheckKind.ProbeResponses),
                Source: null,
                score,
                complaint,
                Duration: null));
        }

        private void CheckLocalHealthCheckParticipants(
            DateTimeOffset now,
            List<LocalSiloHealthEvent> events,
            List<string> complaints)
        {
            for (var i = 0; i < _healthCheckParticipants.Count; i++)
            {
                var participant = _healthCheckParticipants[i];
                var score = 0;
                string? complaint = null;
                try
                {
                    if (!participant.CheckHealth(_lastHealthCheckTime, out var reason))
                    {
                        LogHealthCheckParticipantUnhealthy(participant.GetType(), reason);
                        complaint = $"Health check participant {participant.GetType()} is reporting that it is unhealthy with complaint: {reason}";
                        complaints.Add(complaint);
                        score = 1;
                    }
                }
                catch (Exception exception)
                {
                    LogHealthCheckParticipantError(exception, participant.GetType());
                    complaint = $"Error checking health for participant {participant.GetType()}: {LogFormatter.PrintException(exception)}";
                    complaints.Add(complaint);
                    score = 1;
                }

                events.Add(new(
                    now,
                    LocalSiloHealthCheckKind.HealthCheckParticipant,
                    GetCategory(LocalSiloHealthCheckKind.HealthCheckParticipant),
                    $"{participant.GetType().FullName}[{i}]",
                    score,
                    complaint,
                    Duration: null));
            }

            _lastHealthCheckTime = now.UtcDateTime;
        }

        private LocalSiloHealthStatus AggregateHealthStatus(
            DateTimeOffset start,
            DateTimeOffset end,
            LocalSiloHealthCheckCategory categories)
        {
            if (categories == LocalSiloHealthCheckCategory.None)
            {
                return new(0, []);
            }

            var events = _healthEvents
                .Where(status => status.Timestamp >= start
                    && status.Timestamp <= end
                    && (status.Category & categories) != 0)
                .GroupBy(static status => (status.Kind, status.Source))
                .Select(static statuses => statuses
                    .OrderByDescending(static status => status.Score)
                    .ThenByDescending(static status => status.Timestamp)
                    .First())
                .OrderBy(static status => status.Kind)
                .ThenBy(static status => status.Source, StringComparer.Ordinal)
                .ToImmutableArray();
            var score = Math.Clamp(events.Sum(static status => status.Score), 0, MaxScore);
            return new(score, events);
        }

        private void RemoveExpiredEvents(DateTimeOffset now)
        {
            var oldestRetained = now - HistoryDuration;
            _healthEvents.RemoveAll(status => status.Timestamp < oldestRetained);
        }

        private static LocalSiloHealthCheckCategory GetCategory(LocalSiloHealthCheckKind kind)
            => kind switch
            {
                LocalSiloHealthCheckKind.MembershipStatus
                    or LocalSiloHealthCheckKind.SiloSuspected
                    or LocalSiloHealthCheckKind.ProbeRequests
                    or LocalSiloHealthCheckKind.ProbeResponses => LocalSiloHealthCheckCategory.Network,
                _ => LocalSiloHealthCheckCategory.Local,
            };

        private LocalSiloHealthStatus GetLatestHealthStatus()
        {
            var now = _timeProvider.GetUtcNow();
            lock (_samplingLock)
            {
                return EnsureHealthCheck(now, _timeProvider.GetTimestamp());
            }
        }

        private async Task Run()
        {
            while (await _degradationCheckTimer.NextTick())
            {
                try
                {
                    var status = GetLatestHealthStatus();
                    if (status.Score > 0)
                    {
                        var complaintsString = string.Join("\n", status.Complaints);
                        LogSelfMonitoringDegraded(status.Score, MaxScore, complaintsString);
                    }
                }
                catch (Exception exception)
                {
                    LogErrorMonitoringLocalSiloHealth(exception);
                }
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(ServiceLifecycleStage.Active, this);
        }

        public Task OnStart(CancellationToken ct)
        {
            _runTask = Task.Run(this.Run);
            _isActive = true;
            return Task.CompletedTask;
        }

        public async Task OnStop(CancellationToken ct)
        {
            _degradationCheckTimer.Dispose();
            _isActive = false;

            if (_runTask is Task task)
            {
                await task.WaitAsync(ct).SuppressThrowing();
            }
        }

        /// <summary>
        /// Measures queue delay on the .NET <see cref="ThreadPool"/>.
        /// </summary>
        private class ThreadPoolMonitor
        {
            private static readonly WaitCallback Callback = state => ((ThreadPoolMonitor)state!).Execute();
#if NET9_0_OR_GREATER
            private readonly Lock _lockObj = new();
#else
            private readonly object _lockObj = new();
#endif
            private readonly ILogger<ThreadPoolMonitor> _log;
            private readonly TimeProvider _timeProvider;
            private bool _scheduled;
            private TimeSpan _lastQueueDelay;
            private long _queueDelayTimestamp;

            public ThreadPoolMonitor(ILogger<ThreadPoolMonitor> log, TimeProvider timeProvider)
            {
                _log = log;
                _timeProvider = timeProvider;
            }

            public TimeSpan MeasureQueueDelay()
            {
                bool shouldSchedule;
                TimeSpan delay;
                lock (_lockObj)
                {
                    var currentQueueDelay = _scheduled ? _timeProvider.GetElapsedTime(_queueDelayTimestamp) : TimeSpan.Zero;
                    delay = currentQueueDelay > _lastQueueDelay ? currentQueueDelay : _lastQueueDelay;

                    if (!_scheduled)
                    {
                        _scheduled = true;
                        shouldSchedule = true;
                        _queueDelayTimestamp = _timeProvider.GetTimestamp();
                    }
                    else
                    {
                        shouldSchedule = false;
                    }
                }

                if (shouldSchedule)
                {
                    _ = ThreadPool.UnsafeQueueUserWorkItem(Callback, this);
                }

                return delay;
            }

            private void Execute()
            {
                try
                {
                    lock (_lockObj)
                    {
                        _scheduled = false;
                        _lastQueueDelay = _timeProvider.GetElapsedTime(_queueDelayTimestamp);
                    }
                }
                catch (Exception exception)
                {
                    LocalSiloHealthMonitor.LogThreadPoolDelayMonitorError(_log, exception);
                }
            }
        }

        [LoggerMessage(
            Message = ".NET Thread Pool is exhibiting delays of {ThreadPoolQueueDelaySeconds}s. This can indicate .NET Thread Pool starvation, very long .NET GC pauses, or other runtime or machine pauses."
        )]
        private partial void LogThreadPoolDelay(LogLevel logLevel, double threadPoolQueueDelaySeconds);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "This silo is not active (Status: {Status}) and is therefore not healthy."
        )]
        private partial void LogSiloNotActive(SiloStatus status);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Silo {Silo} recently suspected this silo is dead at {SuspectingTime}."
        )]
        private partial void LogSiloSuspected(SiloAddress silo, DateTime suspectingTime);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Could not find a membership entry for this silo"
        )]
        private partial void LogMembershipEntryNotFound();

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Health check participant {Participant} is reporting that it is unhealthy with complaint: {Reason}"
        )]
        private partial void LogHealthCheckParticipantUnhealthy(Type participant, string reason);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error checking health for {Participant}"
        )]
        private partial void LogHealthCheckParticipantError(Exception exception, Type participant);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Self-monitoring determined that local health is degraded. Degradation score is {Score}/{MaxScore} (lower is better). Complaints: {Complaints}"
        )]
        private partial void LogSelfMonitoringDegraded(int score, int maxScore, string complaints);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Exception monitoring .NET thread pool delay"
        )]
        private static partial void LogThreadPoolDelayMonitorError(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error while monitoring local silo health"
        )]
        private partial void LogErrorMonitoringLocalSiloHealth(Exception exception);
    }
}
