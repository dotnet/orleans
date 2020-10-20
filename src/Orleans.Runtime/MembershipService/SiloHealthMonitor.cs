using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using static Orleans.Runtime.MembershipService.SiloHealthMonitor;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for monitoring an individual remote silo.
    /// </summary>
    internal class SiloHealthMonitor : ITestAccessor, IHealthCheckable
    {
        private readonly ILogger _log;
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IRemoteSiloProber _prober;
        private readonly ILocalSiloHealthMonitor _localSiloHealthMonitor;
        private readonly CancellationTokenSource _stoppingCancellation = new CancellationTokenSource();
        private readonly object _lockObj = new object();
        private readonly IAsyncTimer _pingTimer;
        private Func<SiloHealthMonitor, ProbeResult, Task> _onProbeResult;
        private Task _runTask;

        /// <summary>
        /// The id of the next probe.
        /// </summary>
        private long _nextProbeId;

        /// <summary>
        /// The highest internal probe number which has completed.
        /// </summary>
        private long _highestCompletedProbeId = -1;

        /// <summary>
        /// The number of failed probes since the last successful probe.
        /// </summary>
        private int _failedProbes;

        /// <summary>
        /// The time that the last ping response was received from either the node being monitored or an intermediary.
        /// </summary>
        public DateTime LastResponse { get; private set; }

        /// <summary>
        /// The duration of time measured from just prior to sending the last probe which received a response until just after receiving and processing the response.
        /// </summary>
        public TimeSpan LastRoundTripTime { get; private set; }

        public SiloHealthMonitor(
            SiloAddress siloAddress,
            Func<SiloHealthMonitor, ProbeResult, Task> onProbeResult,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            ILoggerFactory loggerFactory,
            IRemoteSiloProber remoteSiloProber,
            IAsyncTimerFactory asyncTimerFactory,
            ILocalSiloHealthMonitor localSiloHealthMonitor)
        {
            SiloAddress = siloAddress;
            _clusterMembershipOptions = clusterMembershipOptions.Value;
            _prober = remoteSiloProber;
            _localSiloHealthMonitor = localSiloHealthMonitor;
            _log = loggerFactory.CreateLogger<SiloHealthMonitor>();
            _pingTimer = asyncTimerFactory.Create(
                _clusterMembershipOptions.ProbeTimeout,
                nameof(SiloHealthMonitor));
            _onProbeResult = onProbeResult;
        }

        internal interface ITestAccessor
        {
            int MissedProbes { get; }
            Func<SiloHealthMonitor, ProbeResult, Task> OnProbeResult { get; set; }
        }

        /// <summary>
        /// The silo which this instance is responsible for.
        /// </summary>
        public SiloAddress SiloAddress { get; }

        /// <summary>
        /// Whether or not this monitor is canceled.
        /// </summary>
        public bool IsCanceled => _stoppingCancellation.IsCancellationRequested;

        int ITestAccessor.MissedProbes => _failedProbes;

        Func<SiloHealthMonitor, ProbeResult, Task> ITestAccessor.OnProbeResult { get => _onProbeResult; set => _onProbeResult = value; }

        /// <summary>
        /// Start the monitor.
        /// </summary>
        public void Start()
        {
            lock (_lockObj)
            {
                if (_stoppingCancellation.IsCancellationRequested)
                {
                    throw new InvalidOperationException("This instance has already been stopped and cannot be started again");
                }

                if (_runTask is Task)
                {
                    throw new InvalidOperationException("This instance has already been started");
                }

                _runTask = Task.Run(Run);
            }
        }

        /// <summary>
        /// Stop the monitor.
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_lockObj)
            {
                if (_stoppingCancellation.IsCancellationRequested)
                {
                    return;
                }

                _stoppingCancellation.Cancel();
                _pingTimer.Dispose();
            }

            if (_runTask is Task task)
            {
                await Task.WhenAny(task, cancellationToken.WhenCancelled());
            }
        }

        private async Task Run()
        {
            var random = new SafeRandom();
            TimeSpan? overrideDelay = random.NextTimeSpan(_clusterMembershipOptions.ProbeTimeout);
            while (await _pingTimer.NextTick(overrideDelay))
            {
                overrideDelay = default;
            
                try
                {
                    TimeSpan timeout;
                    if (_clusterMembershipOptions.ExtendProbeTimeoutDuringDegradation)
                    {
                        var localDegradationScore = _localSiloHealthMonitor.GetLocalHealthDegradationScore(DateTime.UtcNow);

                        // Probe the silo directly.
                        // Attempt to account for local health degradation by extending the timeout period.
                        timeout = _clusterMembershipOptions.ProbeTimeout.Multiply(1 + localDegradationScore);
                    }
                    else
                    {
                        timeout = _clusterMembershipOptions.ProbeTimeout;
                    }

                    var cancellation = new CancellationTokenSource(timeout);
                    var probeResult = await this.ProbeDirectly(cancellation.Token).ConfigureAwait(false);

                    await _onProbeResult(this, probeResult).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _log.LogError(exception, "Exception monitoring silo {SiloAddress}", SiloAddress);
                }
            }
        }

        /// <summary>
        /// Probes the remote silo.
        /// </summary>
        /// <param name="cancellation">A token to cancel and fail the probe attempt.</param>
        /// <returns>The number of failed probes since the last successful probe.</returns>
        private async Task<ProbeResult> ProbeDirectly(CancellationToken cancellation)
        {
            var id = (int)Interlocked.Increment(ref _nextProbeId);

            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace("Going to send Ping #{Id} to probe silo {Silo}", id, SiloAddress);
            }

            var roundTripTimer = ValueStopwatch.StartNew();
            ProbeResult probeResult;
            Exception failureException;
            try
            {
                var probeCancellation = cancellation.WhenCancelled();
                var probeTask = _prober.Probe(SiloAddress, id);
                var task = await Task.WhenAny(probeCancellation, probeTask);

                if (ReferenceEquals(task, probeCancellation))
                {
                    probeTask.Ignore();
                    failureException = new OperationCanceledException($"The ping attempt was cancelled after {roundTripTimer.Elapsed}. Ping #{id}");
                }
                else
                {
                    await probeTask;
                    failureException = null;
                }
            }
            catch (Exception exception)
            {
                failureException = exception;
            }
            finally
            {
                roundTripTimer.Stop();
            }

            if (failureException is null)
            {
                MessagingStatisticsGroup.OnPingReplyReceived(SiloAddress);

                lock (_lockObj)
                {
                    if (id <= _highestCompletedProbeId)
                    {
                        _log.LogInformation(
                            "Ignoring success result for ping #{Id} from {Silo} in {RoundTripTime} since a later probe has already completed. Highest ({HighestCompletedProbeId}) > Current ({CurrentProbeId})",
                            id,
                            SiloAddress,
                            roundTripTimer.Elapsed,
                            _highestCompletedProbeId,
                            id);
                        probeResult = new ProbeResult(_failedProbes, ProbeResultStatus.Unknown);
                    }
                    else if (_stoppingCancellation.IsCancellationRequested)
                    {
                        _log.LogInformation(
                            "Ignoring success result for ping #{Id} from {Silo} in {RoundTripTime} since this monitor has been stopped",
                            id,
                            SiloAddress,
                            roundTripTimer.Elapsed);
                        probeResult = new ProbeResult(_failedProbes, ProbeResultStatus.Unknown);
                    }
                    else
                    {
                        if (_log.IsEnabled(LogLevel.Trace))
                        {
                            _log.LogTrace(
                                "Got successful ping response for ping #{Id} from {Silo} with round trip time of {RoundTripTime}",
                                id,
                                SiloAddress,
                                roundTripTimer.Elapsed);
                        }

                        _highestCompletedProbeId = id;
                        Interlocked.Exchange(ref _failedProbes, 0);
                        LastResponse = DateTime.UtcNow;
                        LastRoundTripTime = roundTripTimer.Elapsed;
                        probeResult = new ProbeResult(0, ProbeResultStatus.Succeeded);
                    }
                }
            }
            else
            {
                MessagingStatisticsGroup.OnPingReplyMissed(SiloAddress);

                lock (_lockObj)
                {
                    if (id <= _highestCompletedProbeId)
                    {
                        _log.LogInformation(
                            failureException,
                            "Ignoring failure result for probe #{Id} to {Silo} since a later probe has already completed. Highest completed probe id is {HighestCompletedProbeId}",
                            id,
                            SiloAddress,
                            _highestCompletedProbeId);
                        probeResult = new ProbeResult(_failedProbes, ProbeResultStatus.Unknown);
                    }
                    else if (_stoppingCancellation.IsCancellationRequested)
                    {
                        _log.LogInformation(
                            failureException,
                            "Ignoring failure result for probe #{Id} to {Silo} since this monitor has been stopped",
                            id,
                            SiloAddress);
                        probeResult = new ProbeResult(_failedProbes, ProbeResultStatus.Unknown);
                    }
                    else
                    {
                        _highestCompletedProbeId = id;
                        var failedProbes = Interlocked.Increment(ref _failedProbes);

                        _log.LogWarning(
                            (int)ErrorCode.MembershipMissedPing,
                            failureException,
                            "Did not get response for probe # Current number of consecutive failed probes is {FailedProbeCount}",
                            id,
                            SiloAddress,
                            roundTripTimer.Elapsed,
                            failedProbes);

                        probeResult = new ProbeResult(failedProbes, ProbeResultStatus.Failed);
                    }
                }
            }

            return probeResult;
        }

        /// <inheritdoc />
        public bool CheckHealth(DateTime lastCheckTime) => _pingTimer.CheckHealth(lastCheckTime);

        /// <summary>
        /// Represents the result of probing a silo.
        /// </summary>
        public readonly struct ProbeResult
        {
            public ProbeResult(int failedProbeCount, ProbeResultStatus status)
            {
                this.FailedProbeCount = failedProbeCount;
                this.Status = status;
            }

            public int FailedProbeCount { get; }

            public ProbeResultStatus Status { get; }
        }

        public enum ProbeResultStatus
        {
            Unknown,
            Failed,
            Succeeded
        }
    }
}
