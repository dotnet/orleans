using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using static Orleans.Runtime.MembershipService.SiloHealthMonitor;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for monitoring an individual remote silo.
    /// </summary>
    internal class SiloHealthMonitor : ITestAccessor, IHealthCheckable
    {
        private readonly ILogger _log;
        private readonly IOptionsMonitor<ClusterMembershipOptions> _clusterMembershipOptions;
        private readonly IRemoteSiloProber _prober;
        private readonly ILocalSiloHealthMonitor _localSiloHealthMonitor;
        private readonly IClusterMembershipService _membershipService;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly CancellationTokenSource _stoppingCancellation = new CancellationTokenSource();
        private readonly object _lockObj = new object();
        private readonly IAsyncTimer _pingTimer;
        private ValueStopwatch _elapsedSinceLastSuccessfulResponse;
        private Func<SiloHealthMonitor, ProbeResult, Task> _onProbeResult;
        private Task _runTask;

        /// <summary>
        /// The id of the next probe.
        /// </summary>
        private int _nextProbeId;

        /// <summary>
        /// The number of failed probes since the last successful probe.
        /// </summary>
        private int _failedProbes;

        /// <summary>
        /// The time since the last ping response was received from either the node being monitored or an intermediary.
        /// </summary>
        public TimeSpan? ElapsedSinceLastResponse => _elapsedSinceLastSuccessfulResponse.IsRunning ? (Nullable<TimeSpan>)_elapsedSinceLastSuccessfulResponse.Elapsed : null;

        /// <summary>
        /// The duration of time measured from just prior to sending the last probe which received a response until just after receiving and processing the response.
        /// </summary>
        public TimeSpan LastRoundTripTime { get; private set; }

        public SiloHealthMonitor(
            SiloAddress siloAddress,
            Func<SiloHealthMonitor, ProbeResult, Task> onProbeResult,
            IOptionsMonitor<ClusterMembershipOptions> clusterMembershipOptions,
            ILoggerFactory loggerFactory,
            IRemoteSiloProber remoteSiloProber,
            IAsyncTimerFactory asyncTimerFactory,
            ILocalSiloHealthMonitor localSiloHealthMonitor,
            IClusterMembershipService membershipService,
            ILocalSiloDetails localSiloDetails)
        {
            SiloAddress = siloAddress;
            _clusterMembershipOptions = clusterMembershipOptions;
            _prober = remoteSiloProber;
            _localSiloHealthMonitor = localSiloHealthMonitor;
            _membershipService = membershipService;
            _localSiloDetails = localSiloDetails;
            _log = loggerFactory.CreateLogger<SiloHealthMonitor>();
            _pingTimer = asyncTimerFactory.Create(
                _clusterMembershipOptions.CurrentValue.ProbeTimeout,
                nameof(SiloHealthMonitor));
            _onProbeResult = onProbeResult;
            _elapsedSinceLastSuccessfulResponse = ValueStopwatch.StartNew();
        }

        internal interface ITestAccessor
        {
            int MissedProbes { get; }
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

        /// <summary>
        /// Start the monitor.
        /// </summary>
        public void Start()
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor();
            lock (_lockObj)
            {
                if (_stoppingCancellation.IsCancellationRequested)
                {
                    throw new InvalidOperationException("This instance has already been stopped and cannot be started again");
                }

                if (_runTask is not null)
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
            ClusterMembershipSnapshot activeMembersSnapshot = default;
            SiloAddress[] otherNodes = default;
            TimeSpan? overrideDelay = RandomTimeSpan.Next(_clusterMembershipOptions.CurrentValue.ProbeTimeout);
            while (await _pingTimer.NextTick(overrideDelay))
            {
                ProbeResult probeResult;
                overrideDelay = default;

                try
                {
                    // Discover the other active nodes in the cluster, if there are any.
                    var membershipSnapshot = _membershipService.CurrentSnapshot;
                    if (otherNodes is null || !object.ReferenceEquals(activeMembersSnapshot, membershipSnapshot))
                    {
                        activeMembersSnapshot = membershipSnapshot;
                        otherNodes = membershipSnapshot.Members.Values
                            .Where(v => v.Status == SiloStatus.Active && v.SiloAddress != this.SiloAddress && v.SiloAddress != _localSiloDetails.SiloAddress)
                            .Select(s => s.SiloAddress)
                            .ToArray();
                    }

                    var isDirectProbe = !_clusterMembershipOptions.CurrentValue.EnableIndirectProbes || _failedProbes < _clusterMembershipOptions.CurrentValue.NumMissedProbesLimit - 1 || otherNodes.Length == 0;
                    var timeout = GetTimeout(isDirectProbe);
                    var cancellation = new CancellationTokenSource(timeout);

                    if (isDirectProbe)
                    {
                        // Probe the silo directly.
                        probeResult = await this.ProbeDirectly(cancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        // Pick a random other node and probe the target indirectly, using the selected node as an intermediary.
                        var intermediary = otherNodes[Random.Shared.Next(otherNodes.Length)];

                        // Select a timeout which will allow the intermediary node to attempt to probe the target node and still respond to this node
                        // if the remote node does not respond in time.
                        // Attempt to account for local health degradation by extending the timeout period.
                        probeResult = await this.ProbeIndirectly(intermediary, timeout, cancellation.Token).ConfigureAwait(false);

                        // If the intermediary is not entirely healthy, remove it from consideration and continue to probe.
                        // Note that all recused silos will be included in the consideration set the next time cluster membership changes.
                        if (probeResult.Status != ProbeResultStatus.Succeeded && probeResult.IntermediaryHealthDegradationScore > 0)
                        {
                            _log.LogInformation("Recusing unhealthy intermediary {Intermediary} and trying again with remaining nodes", intermediary);
                            otherNodes = otherNodes.Where(node => !node.Equals(intermediary)).ToArray();
                            overrideDelay = TimeSpan.FromMilliseconds(250);
                        }
                    }

                    if (!_stoppingCancellation.IsCancellationRequested)
                    {
                        await _onProbeResult(this, probeResult).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    _log.LogError(exception, "Exception monitoring silo {SiloAddress}", SiloAddress);
                }
            }

            TimeSpan GetTimeout(bool isDirectProbe)
            {
                var additionalTimeout = 0;

                if (_clusterMembershipOptions.CurrentValue.ExtendProbeTimeoutDuringDegradation)
                {
                    // Attempt to account for local health degradation by extending the timeout period.
                    var localDegradationScore = _localSiloHealthMonitor.GetLocalHealthDegradationScore(DateTime.UtcNow);
                    additionalTimeout += localDegradationScore;
                }

                if (!isDirectProbe)
                {
                    // Indirect probes need extra time to account for the additional hop.
                    additionalTimeout += 1;
                }

                return _clusterMembershipOptions.CurrentValue.ProbeTimeout.Multiply(1 + additionalTimeout);
            }
        }

        /// <summary>
        /// Probes the remote silo.
        /// </summary>
        /// <param name="cancellation">A token to cancel and fail the probe attempt.</param>
        /// <returns>The number of failed probes since the last successful probe.</returns>
        private async Task<ProbeResult> ProbeDirectly(CancellationToken cancellation)
        {
            var id = ++_nextProbeId;
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

                if (ReferenceEquals(task, probeCancellation) && probeTask.Status != TaskStatus.RanToCompletion)
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
                MessagingInstruments.OnPingReplyReceived(SiloAddress);

                if (_log.IsEnabled(LogLevel.Trace))
                {
                    _log.LogTrace(
                        "Got successful ping response for ping #{Id} from {Silo} with round trip time of {RoundTripTime}",
                        id,
                        SiloAddress,
                        roundTripTimer.Elapsed);
                }

                _failedProbes = 0;
                _elapsedSinceLastSuccessfulResponse.Restart();
                LastRoundTripTime = roundTripTimer.Elapsed;
                probeResult = ProbeResult.CreateDirect(0, ProbeResultStatus.Succeeded);
            }
            else
            {
                MessagingInstruments.OnPingReplyMissed(SiloAddress);

                var failedProbes = ++_failedProbes;
                _log.LogWarning(
                    (int)ErrorCode.MembershipMissedPing,
                    failureException,
                    "Did not get response for probe #{Id} to silo {Silo} after {Elapsed}. Current number of consecutive failed probes is {FailedProbeCount}",
                    id,
                    SiloAddress,
                    roundTripTimer.Elapsed,
                    failedProbes);

                probeResult = ProbeResult.CreateDirect(failedProbes, ProbeResultStatus.Failed);
            }

            return probeResult;
        }

        /// <summary>
        /// Probes the remote node via an intermediary silo.
        /// </summary>
        /// <param name="intermediary">The node to probe the target with.</param>
        /// <param name="directProbeTimeout">The amount of time which the intermediary should allow for the target to respond.</param>
        /// <param name="cancellation">A token to cancel and fail the probe attempt.</param>
        /// <returns>The number of failed probes since the last successful probe.</returns>
        private async Task<ProbeResult> ProbeIndirectly(SiloAddress intermediary, TimeSpan directProbeTimeout, CancellationToken cancellation)
        {
            var id = ++_nextProbeId;
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace("Going to send indirect ping #{Id} to probe silo {Silo} via {Intermediary}", id, SiloAddress, intermediary);
            }

            var roundTripTimer = ValueStopwatch.StartNew();
            ProbeResult probeResult;
            try
            {
                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stoppingCancellation.Token);
                var cancellationTask = cancellationSource.Token.WhenCancelled();
                var probeTask = _prober.ProbeIndirectly(intermediary, SiloAddress, directProbeTimeout, id);
                var task = await Task.WhenAny(cancellationTask, probeTask);

                if (ReferenceEquals(task, cancellationTask) && probeTask.Status != TaskStatus.RanToCompletion)
                {
                    probeTask.Ignore();
                    probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, default);
                }
                else
                {
                    var indirectResult = await probeTask;
                    roundTripTimer.Stop();
                    var roundTripTime = roundTripTimer.Elapsed - indirectResult.ProbeResponseTime;

                    // Record timing regardless of the result.
                    _elapsedSinceLastSuccessfulResponse.Restart();
                    LastRoundTripTime = roundTripTimer.Elapsed - indirectResult.ProbeResponseTime;

                    if (indirectResult.Succeeded)
                    {
                        _log.LogInformation(
                            "Indirect probe request #{Id} to silo {SiloAddress} via silo {IntermediarySiloAddress} succeeded after {RoundTripTime} with a direct probe response time of {ProbeResponseTime}.",
                            id,
                            SiloAddress,
                            intermediary,
                            roundTripTimer.Elapsed,
                            indirectResult.ProbeResponseTime);

                        MessagingInstruments.OnPingReplyReceived(SiloAddress);

                        _failedProbes = 0;
                        probeResult = ProbeResult.CreateIndirect(0, ProbeResultStatus.Succeeded, indirectResult);
                    }
                    else
                    {
                        MessagingInstruments.OnPingReplyMissed(SiloAddress);

                        if (indirectResult.IntermediaryHealthScore > 0)
                        {
                            _log.LogInformation(
                                "Ignoring failure result for ping #{Id} from {Silo} since the intermediary used to probe the silo is not healthy. Intermediary health degradation score: {IntermediaryHealthScore}",
                                id,
                                SiloAddress,
                                indirectResult.IntermediaryHealthScore);
                            probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, indirectResult);
                        }
                        else
                        {
                            _log.LogWarning(
                                "Indirect probe request #{Id} to silo {SiloAddress} via silo {IntermediarySiloAddress} failed after {RoundTripTime} with a direct probe response time of {ProbeResponseTime}. Failure message: {FailureMessage}. Intermediary health score: {IntermediaryHealthScore}",
                                id,
                                SiloAddress,
                                intermediary,
                                roundTripTimer.Elapsed,
                                indirectResult.ProbeResponseTime,
                                indirectResult.FailureMessage,
                                indirectResult.IntermediaryHealthScore);

                            var missed = ++_failedProbes;
                            probeResult = ProbeResult.CreateIndirect(missed, ProbeResultStatus.Failed, indirectResult);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception, "Indirect probe request failed");
                probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, default);
            }

            return probeResult;
        }

        /// <inheritdoc />
        public bool CheckHealth(DateTime lastCheckTime, out string reason) => _pingTimer.CheckHealth(lastCheckTime, out reason);

        /// <summary>
        /// Represents the result of probing a silo.
        /// </summary>
        [StructLayout(LayoutKind.Auto)]
        public readonly struct ProbeResult
        {
            private ProbeResult(int failedProbeCount, ProbeResultStatus status, bool isDirectProbe, int intermediaryHealthDegradationScore)
            {
                FailedProbeCount = failedProbeCount;
                Status = status;
                IsDirectProbe = isDirectProbe;
                IntermediaryHealthDegradationScore = intermediaryHealthDegradationScore;
            }

            public static ProbeResult CreateDirect(int failedProbeCount, ProbeResultStatus status)
                => new ProbeResult(failedProbeCount, status, isDirectProbe: true, 0);

            public static ProbeResult CreateIndirect(int failedProbeCount, ProbeResultStatus status, IndirectProbeResponse indirectProbeResponse)
                => new ProbeResult(failedProbeCount, status, isDirectProbe: false, indirectProbeResponse.IntermediaryHealthScore);

            public int FailedProbeCount { get; }

            public ProbeResultStatus Status { get; }

            public bool IsDirectProbe { get; }

            public int IntermediaryHealthDegradationScore { get; }
        }

        public enum ProbeResultStatus : byte
        {
            Unknown,
            Failed,
            Succeeded
        }
    }
}
