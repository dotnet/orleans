#nullable enable
using System;
using System.Diagnostics;
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
    internal class SiloHealthMonitor : ITestAccessor, IHealthCheckable, IDisposable, IAsyncDisposable
    {
        private readonly ILogger _log;
        private readonly IOptionsMonitor<ClusterMembershipOptions> _clusterMembershipOptions;
        private readonly IRemoteSiloProber _prober;
        private readonly ILocalSiloHealthMonitor _localSiloHealthMonitor;
        private readonly MembershipTableManager _membershipService;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly CancellationTokenSource _stoppingCancellation = new();
        private readonly object _lockObj = new();
        private readonly IAsyncTimer _pingTimer;
        private ValueStopwatch _elapsedSinceLastSuccessfulResponse;
        private readonly Func<SiloHealthMonitor, ProbeResult, Task> _onProbeResult;
        private Task? _runTask;

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
        public TimeSpan? ElapsedSinceLastResponse => _elapsedSinceLastSuccessfulResponse.IsRunning ? (TimeSpan?)_elapsedSinceLastSuccessfulResponse.Elapsed : null;

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
            MembershipTableManager membershipService,
            ILocalSiloDetails localSiloDetails)
        {
            TargetSiloAddress = siloAddress;
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
        public SiloAddress TargetSiloAddress { get; }

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
            Dispose();

            if (_runTask is Task task)
            {
                await task.WaitAsync(cancellationToken).SuppressThrowing();
            }
        }

        public void Dispose()
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
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            if (_runTask is Task task)
            {
                await task.SuppressThrowing();
            }
        }

        private async Task Run()
        {
            MembershipTableSnapshot? activeMembersSnapshot = default;
            SiloAddress[]? otherNodes = default;
            var options = _clusterMembershipOptions.CurrentValue;
            TimeSpan? overrideDelay = RandomTimeSpan.Next(options.ProbeTimeout);
            while (await _pingTimer.NextTick(overrideDelay))
            {
                ProbeResult probeResult;
                overrideDelay = default;
                var now = DateTime.UtcNow;

                try
                {
                    // Discover the other active nodes in the cluster, if there are any.
                    var membershipSnapshot = _membershipService.MembershipTableSnapshot;
                    if (otherNodes is null || !ReferenceEquals(activeMembersSnapshot, membershipSnapshot))
                    {
                        activeMembersSnapshot = membershipSnapshot;
                        otherNodes = membershipSnapshot.Entries.Values
                            .Where(v => v.Status == SiloStatus.Active
                                && !v.SiloAddress.Equals(TargetSiloAddress)
                                && !v.SiloAddress.Equals(_localSiloDetails.SiloAddress)
                                && !v.HasMissedIAmAlives(options, now))
                            .Select(s => s.SiloAddress)
                            .ToArray();
                    }

                    var isDirectProbe = !options.EnableIndirectProbes || _failedProbes < options.NumMissedProbesLimit - 1 || otherNodes.Length == 0;
                    var timeout = GetTimeout(isDirectProbe);
                    using var cancellation = new CancellationTokenSource(timeout);

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
                            _log.LogInformation("Recusing unhealthy intermediary '{Intermediary}' and trying again with remaining nodes", intermediary);
                            otherNodes = [.. otherNodes.Where(node => !node.Equals(intermediary))];
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
                    _log.LogError(exception, "Exception monitoring silo {SiloAddress}", TargetSiloAddress);
                }
            }

            TimeSpan GetTimeout(bool isDirectProbe)
            {
                var additionalTimeout = 0;

                if (options.ExtendProbeTimeoutDuringDegradation)
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

                // When the debugger is attached, extend probe times so that silos are not terminated
                // due to debugging pauses.
                if (Debugger.IsAttached)
                {
                    additionalTimeout += 25;
                }

                return options.ProbeTimeout.Multiply(1 + additionalTimeout);
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
                _log.LogTrace("Going to send Ping #{Id} to probe silo {Silo}", id, TargetSiloAddress);
            }

            var roundTripTimer = ValueStopwatch.StartNew();
            ProbeResult probeResult;
            Exception? failureException;
            try
            {
                await _prober.Probe(TargetSiloAddress, id, cancellation).WaitAsync(cancellation);
                failureException = null;
            }
            catch (OperationCanceledException exception)
            {
                failureException = new OperationCanceledException($"The ping attempt was cancelled after {roundTripTimer.Elapsed}. Ping #{id}", exception);
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
                MessagingInstruments.OnPingReplyReceived(TargetSiloAddress);

                if (_log.IsEnabled(LogLevel.Trace))
                {
                    _log.LogTrace(
                        "Got successful ping response for ping #{Id} from {Silo} with round trip time of {RoundTripTime}",
                        id,
                        TargetSiloAddress,
                        roundTripTimer.Elapsed);
                }

                _failedProbes = 0;
                _elapsedSinceLastSuccessfulResponse.Restart();
                LastRoundTripTime = roundTripTimer.Elapsed;
                probeResult = ProbeResult.CreateDirect(0, ProbeResultStatus.Succeeded);
            }
            else
            {
                MessagingInstruments.OnPingReplyMissed(TargetSiloAddress);

                var failedProbes = ++_failedProbes;
                _log.LogWarning(
                    (int)ErrorCode.MembershipMissedPing,
                    failureException,
                    "Did not get response for probe #{Id} to silo {Silo} after {Elapsed}. Current number of consecutive failed probes is {FailedProbeCount}",
                    id,
                    TargetSiloAddress,
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
                _log.LogTrace("Going to send indirect ping #{Id} to probe silo {Silo} via {Intermediary}", id, TargetSiloAddress, intermediary);
            }

            var roundTripTimer = ValueStopwatch.StartNew();
            ProbeResult probeResult;
            try
            {
                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stoppingCancellation.Token);
                var indirectResult = await _prober.ProbeIndirectly(intermediary, TargetSiloAddress, directProbeTimeout, id, cancellationSource.Token).WaitAsync(cancellationSource.Token);
                roundTripTimer.Stop();
                var roundTripTime = roundTripTimer.Elapsed - indirectResult.ProbeResponseTime;

                // Record timing regardless of the result.
                _elapsedSinceLastSuccessfulResponse.Restart();
                LastRoundTripTime = roundTripTime;

                if (indirectResult.Succeeded)
                {
                    _log.LogInformation(
                        "Indirect probe request #{Id} to silo {SiloAddress} via silo {IntermediarySiloAddress} succeeded after {RoundTripTime} with a direct probe response time of {ProbeResponseTime}.",
                        id,
                        TargetSiloAddress,
                        intermediary,
                        roundTripTimer.Elapsed,
                        indirectResult.ProbeResponseTime);

                    MessagingInstruments.OnPingReplyReceived(TargetSiloAddress);

                    _failedProbes = 0;
                    probeResult = ProbeResult.CreateIndirect(0, ProbeResultStatus.Succeeded, indirectResult, intermediary);
                }
                else
                {
                    MessagingInstruments.OnPingReplyMissed(TargetSiloAddress);

                    if (indirectResult.IntermediaryHealthScore > 0)
                    {
                        _log.LogInformation(
                            "Ignoring failure result for ping #{Id} from {Silo} since the intermediary used to probe the silo is not healthy. Intermediary health degradation score: {IntermediaryHealthScore}.",
                            id,
                            TargetSiloAddress,
                            indirectResult.IntermediaryHealthScore);
                        probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, indirectResult, intermediary);
                    }
                    else
                    {
                        _log.LogWarning(
                            "Indirect probe request #{Id} to silo {SiloAddress} via silo {IntermediarySiloAddress} failed after {RoundTripTime} with a direct probe response time of {ProbeResponseTime}. Failure message: {FailureMessage}. Intermediary health score: {IntermediaryHealthScore}.",
                            id,
                            TargetSiloAddress,
                            intermediary,
                            roundTripTimer.Elapsed,
                            indirectResult.ProbeResponseTime,
                            indirectResult.FailureMessage,
                            indirectResult.IntermediaryHealthScore);

                        var missed = ++_failedProbes;
                        probeResult = ProbeResult.CreateIndirect(missed, ProbeResultStatus.Failed, indirectResult, intermediary);
                    }
                }
            }
            catch (Exception exception)
            {
                MessagingInstruments.OnPingReplyMissed(TargetSiloAddress);
                _log.LogWarning(exception, "Indirect probe request failed.");
                probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, default, intermediary);
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
            private ProbeResult(int failedProbeCount, ProbeResultStatus status, bool isDirectProbe, int intermediaryHealthDegradationScore, SiloAddress? intermediary)
            {
                FailedProbeCount = failedProbeCount;
                Status = status;
                IsDirectProbe = isDirectProbe;
                IntermediaryHealthDegradationScore = intermediaryHealthDegradationScore;
                Intermediary = intermediary;
            }

            public static ProbeResult CreateDirect(int failedProbeCount, ProbeResultStatus status)
                => new(failedProbeCount, status, isDirectProbe: true, 0, null);

            public static ProbeResult CreateIndirect(int failedProbeCount, ProbeResultStatus status, IndirectProbeResponse indirectProbeResponse, SiloAddress? intermediary)
                => new(failedProbeCount, status, isDirectProbe: false, indirectProbeResponse.IntermediaryHealthScore, intermediary);

            public int FailedProbeCount { get; }

            public ProbeResultStatus Status { get; }

            public bool IsDirectProbe { get; }

            public int IntermediaryHealthDegradationScore { get; }

            public SiloAddress? Intermediary { get; }
        }

        public enum ProbeResultStatus
        {
            Unknown,
            Failed,
            Succeeded
        }
    }
}
