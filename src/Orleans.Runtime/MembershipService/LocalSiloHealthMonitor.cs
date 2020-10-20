using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime.MembershipService
{
    internal interface ILocalSiloHealthMonitor
    {
        /// <summary>
        /// Returns the local health degradation score, which is a value between 0 (healthy) and <see cref="LocalSiloHealthMonitor.MaxScore"/> (unhealthy).
        /// </summary>
        /// <param name="checkTime">The time which the check is taking place.</param>
        /// <returns>The local health degradation score, which is a value between 0 (healthy) and <see cref="LocalSiloHealthMonitor.MaxScore"/> (unhealthy).</returns>
        int GetLocalHealthDegradationScore(DateTime checkTime);

        /// <summary>
        /// The most recent list of detected health issues.
        /// </summary>
        ImmutableArray<string> Complaints { get; }
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
    ///   <item>Check that this silos is marked as active in membership.</item>
    ///   <item>Check that no other silo suspects this silo.</item>
    ///   <item>Check for recently received successful ping responses.</item>
    ///   <item>Check for recently received ping requests.</item>
    ///   <item>Check that the .NET Thread Pool is able to process work items within one second.</item>
    ///   <item>Check that local async timers have been firing on-time (within 3 seconds of their due time).</item>
    /// </list>
    /// </remarks>
    internal class LocalSiloHealthMonitor : ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver, ILocalSiloHealthMonitor
    {
        private const int MaxScore = 8;
        private readonly List<IHealthCheckParticipant> _healthCheckParticipants;
        private readonly MembershipTableManager _membershipTableManager;
        private readonly ClusterHealthMonitor _clusterHealthMonitor;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ILogger<LocalSiloHealthMonitor> _log;
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IAsyncTimer _degradationCheckTimer;
        private readonly ThreadPoolMonitor _threadPoolMonitor;
        private readonly ProbeRequestMonitor _probeRequestMonitor; 
        private ValueStopwatch _runTime;
        private Task _runTask;
        private DateTime _lastHealthCheckTime;

        public LocalSiloHealthMonitor(
            IEnumerable<IHealthCheckParticipant> healthCheckParticipants,
            MembershipTableManager membershipTableManager,
            ConnectionManager connectionManager,
            ClusterHealthMonitor clusterHealthMonitor,
            ILocalSiloDetails localSiloDetails,
            ILogger<LocalSiloHealthMonitor> log,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IAsyncTimerFactory timerFactory,
            ILoggerFactory loggerFactory,
            ProbeRequestMonitor probeRequestMonitor)
        {
            _healthCheckParticipants = healthCheckParticipants.ToList();
            _membershipTableManager = membershipTableManager;
            _clusterHealthMonitor = clusterHealthMonitor;
            _localSiloDetails = localSiloDetails;
            _log = log;
            _probeRequestMonitor = probeRequestMonitor;
            _clusterMembershipOptions = clusterMembershipOptions.Value;
            _degradationCheckTimer = timerFactory.Create(
                _clusterMembershipOptions.LocalHealthDegradationMonitoringPeriod,
                nameof(LocalSiloHealthMonitor));
            _threadPoolMonitor = new ThreadPoolMonitor(loggerFactory.CreateLogger<ThreadPoolMonitor>());
            _runTime = ValueStopwatch.StartNew();
        }

        /// <inheritdoc />
        public ImmutableArray<string> Complaints { get; private set; } = ImmutableArray<string>.Empty;

        /// <inheritdoc />
        public int GetLocalHealthDegradationScore(DateTime checkTime) => GetLocalHealthDegradationScore(checkTime, null);

        /// <summary>
        /// Returns the local health degradation score, which is a value between 0 (healthy) and <see cref="MaxScore"/> (unhealthy).
        /// </summary>
        /// <param name="checkTime">The time which the check is taking place.</param>
        /// <param name="complaints">If not null, will be populated with the current set of detected health issues.</param>
        /// <returns>The local health degradation score, which is a value between 0 (healthy) and <see cref="MaxScore"/> (unhealthy).</returns>
        public int GetLocalHealthDegradationScore(DateTime checkTime, List<string> complaints) 
        {
            var score = 0;
            score += CheckSuspectingNodes(checkTime, complaints);
            score += CheckReceivedProbeResponses(checkTime, complaints);
            score += CheckReceivedProbeRequests(checkTime, complaints);
            score += CheckLocalHealthCheckParticipants(checkTime, complaints);
            score += CheckThreadPoolQueueDelay(checkTime, complaints);

            // Clamp the score between 0 and the maximum allowed score.
            score = Math.Max(0, Math.Min(MaxScore, score));
            return score;
        }

        private int CheckThreadPoolQueueDelay(DateTime checkTime, List<string> complaints)
        {
            var threadPoolDelaySeconds = _threadPoolMonitor.MeasureQueueDelay().TotalSeconds;

            if (threadPoolDelaySeconds > 10)
            {
                // Log as an error if the delay is massive.
                _log.LogError(
                    ".NET Thread Pool is exhibiting delays of {ThreadPoolQueueDelaySeconds}s. This can indicate .NET Thread Pool starvation, very long .NET GC pauses, or other runtime or machine pauses.",
                    threadPoolDelaySeconds);
            }
            else if (threadPoolDelaySeconds > 1)
            {
                _log.LogWarning(
                    ".NET Thread Pool is exhibiting delays of {ThreadPoolQueueDelaySeconds}s. This can indicate .NET Thread Pool starvation, very long .NET GC pauses, or other runtime or machine pauses.",
                    threadPoolDelaySeconds);
            }

            complaints?.Add(
                $".NET Thread Pool is exhibiting delays of {threadPoolDelaySeconds}s. This can indicate .NET Thread Pool starvation, very long .NET GC pauses, or other runtime or machine pauses.");

            // Each second of delay contributes to the score.
            return (int)threadPoolDelaySeconds;
        }

        private int CheckSuspectingNodes(DateTime now, List<string> complaints)
        {
            var score = 0;
            var membershipSnapshot = _membershipTableManager.MembershipTableSnapshot;
            if (membershipSnapshot.Entries.TryGetValue(_localSiloDetails.SiloAddress, out var membershipEntry))
            {
                if (membershipEntry.Status != SiloStatus.Active)
                {
                    if (_log.IsEnabled(LogLevel.Warning))
                    {
                        _log.LogWarning("This silo is not active (Status: {Status}) and is therefore not healthy.", membershipEntry.Status);
                        complaints?.Add($"This silo is not active (Status: {membershipEntry.Status} and is therefore not healthy.");
                    }

                    score = MaxScore;
                }

                // Check if there are valid votes against this node.
                var expiration = _clusterMembershipOptions.DeathVoteExpirationTimeout;
                var freshVotes = membershipEntry.GetFreshVotes(now, expiration);
                foreach (var vote in freshVotes)
                {
                    if (membershipSnapshot.GetSiloStatus(vote.Item1) == SiloStatus.Active)
                    {
                        if (_log.IsEnabled(LogLevel.Warning))
                        {
                            _log.LogWarning("Silo {Silo} recently suspected this silo is dead at {SuspectingTime}.", vote.Item1, vote.Item2);
                            complaints?.Add($"Silo {vote.Item1} recently suspected this silo is dead at {vote.Item2}.");
                        }

                        ++score;
                    }
                }
            }
            else
            {
                // If our entry is not found, this node is not healthy.
                if (_log.IsEnabled(LogLevel.Error))
                {
                    _log.LogError("Could not find a membership entry for this silo");
                    complaints?.Add("Could not find a membership entry for this silo");
                }

                score = MaxScore;
            }

            return score;
        }

        private int CheckReceivedProbeRequests(DateTime now, List<string> complaints)
        {
            // Have we received ping REQUESTS from other nodes?
            var recencyWindow = _clusterMembershipOptions.ProbeTimeout.Multiply(_clusterMembershipOptions.NumMissedProbesLimit);
            var score = 0;

            if (_runTime.Elapsed < recencyWindow)
            {
                return 0;
            }

            var membershipSnapshot = _membershipTableManager.MembershipTableSnapshot;
            var sinceLastProbeRequest = _probeRequestMonitor.ElapsedSinceLastProbeRequest;

            // Only consider recency of the last received probe request if there is more than one other node.
            // Otherwise, it may fail to vote another node dead in a one or two node cluster.
            if (sinceLastProbeRequest > recencyWindow && membershipSnapshot.ActiveNodeCount > 2)
            {
                // This node has not received a successful ping response since the window began.
                if (_log.IsEnabled(LogLevel.Warning))
                {
                    var lastRequestTime = now - sinceLastProbeRequest;
                    _log.LogWarning("This silo has not received a probe request since {LastProbeRequest}", lastRequestTime);
                    complaints?.Add($"This silo has not received a probe request since {lastRequestTime}");
                }

                ++score;
            }

            return score;
        }

        private int CheckLocalHealthCheckParticipants(DateTime now, List<string> complaints)
        {
            // Check for execution delays and other local health warning signs.
            var score = 0;
            foreach (var participant in _healthCheckParticipants)
            {
                try
                {
                    if (!participant.CheckHealth(_lastHealthCheckTime))
                    {
                        if (_log.IsEnabled(LogLevel.Warning))
                        {
                            _log.LogWarning("Health check participant {Participant} is reporting that it is unhealthy", participant?.GetType().ToString());
                            complaints?.Add($"Health check participant {participant?.GetType().ToString()}");
                        }

                        ++score;
                    }
                }
                catch (Exception exception)
                {
                    _log.LogError(exception, "Error checking health for {Participant}", participant?.GetType().ToString());
                    complaints?.Add($"Error checking health for participant {participant?.GetType().ToString()}: {LogFormatter.PrintException(exception)}");
                }
            }

            _lastHealthCheckTime = now;
            return score;
        }

        private int CheckReceivedProbeResponses(DateTime now, List<string> complaints)
        {
            var recencyWindow = _clusterMembershipOptions.ProbeTimeout.Multiply(_clusterMembershipOptions.NumMissedProbesLimit);
            if (_runTime.Elapsed < recencyWindow)
            {
                // If the silo has not been live for long enough.
                return 0;
            }

            // Determine how recently the latest successful ping response was received.
            var score = 0;
            var siloMonitors = _clusterHealthMonitor.SiloMonitors;
            var lastSuccessfulResponse = default(DateTime);
            var shortestRoundTripTime = TimeSpan.MaxValue;
            foreach (var monitor in siloMonitors.Values)
            {
                var current = monitor.LastResponse;
                if (current > lastSuccessfulResponse)
                {
                    lastSuccessfulResponse = current;
                }

                if (monitor.LastRoundTripTime < shortestRoundTripTime)
                {
                    shortestRoundTripTime = monitor.LastRoundTripTime;
                }
            }

            // Only consider recency of the last successful ping if this node is monitoring more than one other node.
            // Otherwise, it may fail to vote another node dead in a one or two node cluster.
            if (lastSuccessfulResponse < now - recencyWindow && siloMonitors.Count > 1)
            {
                // This node has not received a successful ping response since the window began.
                if (_log.IsEnabled(LogLevel.Warning))
                {
                    if (lastSuccessfulResponse == default)
                    {
                        _log.LogWarning("This silo has not received any successful probe responses");
                        complaints?.Add("This silo has not received any successful probe responses");
                    }
                    else
                    {
                        _log.LogWarning("This silo has not received a successful probe response since {LastSuccessfulResponse}", lastSuccessfulResponse);
                        complaints?.Add($"This silo has not received a successful probe response since {lastSuccessfulResponse}");
                    }
                }

                ++score;
            }

            return score;
        }

        private async Task Run()
        {
            while (await _degradationCheckTimer.NextTick())
            {
                try
                {
                    var complaints = new List<string>();
                    var now = DateTime.UtcNow;
                    var score = GetLocalHealthDegradationScore(now, complaints);
                    if (score > 0 && _log.IsEnabled(LogLevel.Warning))
                    {
                        var complaintsString = string.Join("\n", complaints);
                        _log.LogWarning("Self-monitoring determined that local health is degraded. Degradation score is {Score}/{MaxScore} (lower is better). Complaints: {Complaints}", score, MaxScore, complaintsString);
                    }

                    this.Complaints = ImmutableArray.CreateRange(complaints);
                }
                catch (Exception exception)
                {
                    _log.LogError(exception, "Error while monitoring local silo health");
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
            _runTime.Restart();
            return Task.CompletedTask;
        }

        public async Task OnStop(CancellationToken ct)
        {
            _degradationCheckTimer.Dispose();

            if (_runTask is Task task)
            {
                await Task.WhenAny(task, ct.WhenCancelled()).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Measures queue delay on the .NET <see cref="ThreadPool"/>.
        /// </summary>
        private class ThreadPoolMonitor
        {
            private static readonly WaitCallback Callback = state => ((ThreadPoolMonitor)state).Execute();
            private readonly object _lockObj = new object();
            private readonly ILogger<ThreadPoolMonitor> _log;
            private bool _scheduled;
            private TimeSpan _lastQueueDelay;
            private ValueStopwatch _queueDelay;

            public ThreadPoolMonitor(ILogger<ThreadPoolMonitor> log)
            {
                _log = log;
            }

            public TimeSpan MeasureQueueDelay()
            {
                bool shouldSchedule;
                TimeSpan delay;
                lock (_lockObj)
                {
                    var currentQueueDelay = _queueDelay.Elapsed;
                    delay = currentQueueDelay > _lastQueueDelay ? currentQueueDelay : _lastQueueDelay;

                    if (!_scheduled)
                    {
                        _scheduled = true;
                        shouldSchedule = true;
                        _queueDelay.Restart();
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
                        _queueDelay.Stop();
                        _lastQueueDelay = _queueDelay.Elapsed;
                    }
                }
                catch (Exception exception)
                {
                    _log.LogError(exception, "Exception monitoring .NET thread pool delay");
                }
            }
        }
    }
}
