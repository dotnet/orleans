using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
    internal partial class SiloHealthMonitor : ITestAccessor, IHealthCheckable, IDisposable, IAsyncDisposable
    {
        private static readonly TimeSpan MaxSupportedTimerTimeout = TimeSpan.FromMilliseconds(0xfffffffe);
        private readonly ILogger _log;
        private readonly IOptionsMonitor<ClusterMembershipOptions> _clusterMembershipOptions;
        private readonly IRemoteSiloProber _prober;
        private readonly ILocalSiloHealthMonitor _localSiloHealthMonitor;
        private readonly IMembershipManager _membershipService;
        private readonly MessagingInstruments? _messagingInstruments;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationTokenSource _stoppingCancellation = new();
#if NET9_0_OR_GREATER
        private readonly Lock _lockObj = new();
#else
        private readonly object _lockObj = new();
#endif
        private readonly IAsyncTimer _pingTimer;
        private long? _lastSuccessfulResponseTimestamp;
        private readonly Func<SiloHealthMonitor, ProbeResult, Task> _onProbeResult;
        private PhiAccrualFailureDetector? _failureDetector;
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
        public TimeSpan? ElapsedSinceLastResponse
            => _lastSuccessfulResponseTimestamp is { } timestamp ? _timeProvider.GetElapsedTime(timestamp) : null;

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
            IMembershipManager membershipService,
            ILocalSiloDetails localSiloDetails,
            [FromKeyedServices(TimeProviderNames.Membership)] TimeProvider timeProvider,
            MessagingInstruments? messagingInstruments = null)
        {
            TargetSiloAddress = siloAddress;
            _clusterMembershipOptions = clusterMembershipOptions;
            _prober = remoteSiloProber;
            _localSiloHealthMonitor = localSiloHealthMonitor;
            _membershipService = membershipService;
            _messagingInstruments = messagingInstruments;
            _localSiloDetails = localSiloDetails;
            _timeProvider = timeProvider;
            _log = loggerFactory.CreateLogger<SiloHealthMonitor>();
            _pingTimer = asyncTimerFactory.Create(
                _clusterMembershipOptions.CurrentValue.ProbeTimeout,
                nameof(SiloHealthMonitor),
                timeProvider);
            _onProbeResult = onProbeResult;
            _lastSuccessfulResponseTimestamp = _timeProvider.GetTimestamp();
        }

        internal interface ITestAccessor
        {
            int MissedProbes { get; }
            int ProbeTimeoutSampleCount { get; }
            TimeSpan CurrentProbeTimeout { get; }
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
        int ITestAccessor.ProbeTimeoutSampleCount => _failureDetector?.SampleCount ?? 0;
        TimeSpan ITestAccessor.CurrentProbeTimeout => _failureDetector?.GetTimeout() ?? _clusterMembershipOptions.CurrentValue.ProbeTimeout;

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
            var failureDetector = _failureDetector = new PhiAccrualFailureDetector(options.ProbeTimeout);
            var previousProbePeriod = options.ProbeTimeout;
            TimeSpan? nextProbeDelay = RandomTimeSpan.Next(previousProbePeriod);
            while (await _pingTimer.NextTick(nextProbeDelay))
            {
                ProbeResult probeResult;
                var isDirectProbe = true;
                var localDegradationScore = 0;
                long? probeStartTimestamp = null;
                var now = _timeProvider.GetUtcNow().UtcDateTime;

                try
                {
                    // Discover the other active nodes in the cluster, if there are any.
                    var membershipSnapshot = _membershipService.CurrentSnapshot;
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

                    isDirectProbe = !options.EnableIndirectProbes || _failedProbes < options.NumMissedProbesLimit - 1 || otherNodes.Length == 0;
                    localDegradationScore = GetLocalDegradationScore(previousProbePeriod);
                    var timeout = CalculateProbeTimeout(failureDetector, options, localDegradationScore, isDirectProbe, Debugger.IsAttached);
                    probeStartTimestamp = _timeProvider.GetTimestamp();
                    var stallStartTimestamp = isDirectProbe
                        ? _localSiloHealthMonitor.GetTimestamp()
                        : 0;
                    using var cancellation = new CancellationTokenSource(timeout, _timeProvider);

                    if (isDirectProbe)
                    {
                        // Probe the silo directly.
                        probeResult = await this.ProbeDirectly(cancellation.Token, timeout, stallStartTimestamp).ConfigureAwait(false);
                    }
                    else
                    {
                        // Pick a random other node and probe the target indirectly, using the selected node as an intermediary.
                        var intermediary = otherNodes[Random.Shared.Next(otherNodes.Length)];

                        // Select a timeout which will allow the intermediary node to attempt to probe the target node and still respond to this node
                        // if the remote node does not respond in time.
                        // Attempt to account for local health degradation by extending the timeout period.
                        var directProbeTimeout = CalculateIndirectProbeTargetTimeout(timeout, localDegradationScore);
                        probeResult = await this.ProbeIndirectly(intermediary, directProbeTimeout, cancellation.Token).ConfigureAwait(false);

                        // If the intermediary is not entirely healthy, remove it from consideration and continue to probe.
                        // Note that all recused silos will be included in the consideration set the next time cluster membership changes.
                        if (probeResult.Status != ProbeResultStatus.Succeeded && probeResult.IntermediaryHealthDegradationScore > 0)
                        {
                            LogInformationRecusingUnhealthyIntermediary(_log, intermediary);
                            otherNodes = [.. otherNodes.Where(node => !node.Equals(intermediary))];
                        }
                    }

                    if (!_stoppingCancellation.IsCancellationRequested)
                    {
                        await _onProbeResult(this, probeResult).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    LogErrorExceptionMonitoringSilo(_log, exception, TargetSiloAddress);
                }
                finally
                {
                    previousProbePeriod = CalculateProbeTimeout(
                        failureDetector,
                        options,
                        localDegradationScore,
                        isDirectProbe,
                        Debugger.IsAttached);
                    nextProbeDelay = probeStartTimestamp is { } timestamp
                        ? CalculateNextProbeDelay(previousProbePeriod, _timeProvider.GetElapsedTime(timestamp))
                        : previousProbePeriod;
                }
            }

            int GetLocalDegradationScore(TimeSpan period)
            {
                if (!options.ExtendProbeTimeoutDuringDegradation)
                {
                    return 0;
                }

                // This query must happen before the probe because its result determines the probe timeout.
                // Outcome-reporting health checks, such as an intermediary's health score, run after probing.
                return _localSiloHealthMonitor.GetLocalHealthStatus(
                    period,
                    LocalSiloHealthCheckCategory.Local).Score;
            }
        }

        internal static TimeSpan CalculateProbeTimeout(
            PhiAccrualFailureDetector failureDetector,
            ClusterMembershipOptions options,
            int localDegradationScore,
            bool isDirectProbe,
            bool isDebuggerAttached)
        {
            var extensionFactor = 1 + localDegradationScore;
            if (!isDirectProbe)
            {
                extensionFactor++;
            }

            var timeout = failureDetector.GetTimeout(options.MinProbeTimeout, options.MaxProbeTimeout, extensionFactor);
            if (isDebuggerAttached)
            {
                var debuggerExtensionTicks = failureDetector.GetTimeout().Ticks * 25d;
                var extendedTimeoutTicks = Math.Min(MaxSupportedTimerTimeout.Ticks, timeout.Ticks + debuggerExtensionTicks);
                timeout = TimeSpan.FromTicks((long)extendedTimeoutTicks);
            }

            return timeout;
        }

        internal static TimeSpan CalculateNextProbeDelay(TimeSpan probePeriod, TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probePeriod, TimeSpan.Zero);
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must not be negative.");
            }

            return elapsed < probePeriod ? probePeriod - elapsed : TimeSpan.Zero;
        }

        internal static TimeSpan CalculateIndirectProbeTargetTimeout(TimeSpan timeout, int localDegradationScore)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfNegative(localDegradationScore);

            var extensionFactor = 1 + localDegradationScore;
            var responseAllowanceTicks = Math.Max(1, timeout.Ticks / (extensionFactor + 1));
            return TimeSpan.FromTicks(timeout.Ticks - responseAllowanceTicks);
        }

        /// <summary>
        /// Probes the remote silo.
        /// </summary>
        /// <param name="cancellation">A token to cancel and fail the probe attempt.</param>
        /// <param name="probeTimeout">The timeout used for this probe, for local stall evaluation.</param>
        /// <param name="stallStartTimestamp">The start of the local stall collection interval.</param>
        /// <returns>The number of failed probes since the last successful probe.</returns>
        private async Task<ProbeResult> ProbeDirectly(CancellationToken cancellation, TimeSpan probeTimeout, long stallStartTimestamp)
        {
            var id = ++_nextProbeId;
            LogTraceGoingToSendPing(_log, id, TargetSiloAddress);

            var roundTripTimer = TimeProviderValueStopwatch.StartNew(_timeProvider);
            ProbeResult probeResult;
            Exception? failureException;
            var probeTimedOut = false;
            try
            {
                await _prober.Probe(TargetSiloAddress, id, cancellation).WaitAsync(cancellation);
                failureException = null;
            }
            catch (OperationCanceledException exception)
            {
                probeTimedOut = cancellation.IsCancellationRequested;
                failureException = new OperationCanceledException(
                    $"The ping attempt was cancelled after {roundTripTimer.GetElapsedTime(out _)}. Ping #{id}",
                    exception);
            }
            catch (Exception exception)
            {
                failureException = exception;
            }
            var roundTripTime = roundTripTimer.GetElapsedTime(out _);
            var stallDurationDuring = TimeSpan.Zero;
            if (probeTimedOut)
            {
                var probeEndTimestamp = _localSiloHealthMonitor.GetTimestamp();
                stallDurationDuring = await _localSiloHealthMonitor.GetStallDurationAsync(
                    stallStartTimestamp,
                    probeEndTimestamp,
                    _stoppingCancellation.Token).ConfigureAwait(false);
            }
            if (failureException is null)
            {
                _messagingInstruments?.OnPingReplyReceived(TargetSiloAddress);

                LogTraceGotSuccessfulPingResponse(_log, id, TargetSiloAddress, roundTripTime);

                _failedProbes = 0;
                _lastSuccessfulResponseTimestamp = _timeProvider.GetTimestamp();
                LastRoundTripTime = roundTripTime;
                _failureDetector!.RecordResponseTime(roundTripTime);
                probeResult = ProbeResult.CreateDirect(0, ProbeResultStatus.Succeeded);
            }
            else
            {
                // Check if a local stall consumed a significant portion of the probe timeout.
                // If so, the local silo may have been unable to process the response in time,
                // so we treat this as an inconclusive result rather than a failure.
                if (probeTimedOut && stallDurationDuring >= probeTimeout.Multiply(0.25))
                {
                    LogWarningProbeFailureDuringLocalStall(_log, id, TargetSiloAddress, roundTripTime, stallDurationDuring, _failedProbes);
                    probeResult = ProbeResult.CreateDirect(_failedProbes, ProbeResultStatus.Unknown);
                }
                else
                {
                    _messagingInstruments?.OnPingReplyMissed(TargetSiloAddress);
                    var failedProbes = ++_failedProbes;
                    LogWarningDidNotGetResponseForProbe(_log, failureException, id, TargetSiloAddress, roundTripTime, failedProbes);

                    probeResult = ProbeResult.CreateDirect(failedProbes, ProbeResultStatus.Failed);
                }
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
            LogTraceGoingToSendIndirectPing(_log, id, TargetSiloAddress, intermediary);

            var roundTripTimer = TimeProviderValueStopwatch.StartNew(_timeProvider);
            ProbeResult probeResult;
            try
            {
                using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stoppingCancellation.Token);
                var indirectResult = await _prober.ProbeIndirectly(intermediary, TargetSiloAddress, directProbeTimeout, id, cancellationSource.Token).WaitAsync(cancellationSource.Token);
                var elapsed = roundTripTimer.GetElapsedTime(out _);
                var roundTripTime = elapsed - indirectResult.ProbeResponseTime;

                // Record timing regardless of the result.
                _lastSuccessfulResponseTimestamp = _timeProvider.GetTimestamp();
                LastRoundTripTime = roundTripTime;

                if (indirectResult.Succeeded)
                {
                    LogInformationIndirectProbeSucceeded(_log, id, TargetSiloAddress, intermediary, elapsed, indirectResult.ProbeResponseTime);

                    _messagingInstruments?.OnPingReplyReceived(TargetSiloAddress);

                    _failedProbes = 0;
                    probeResult = ProbeResult.CreateIndirect(0, ProbeResultStatus.Succeeded, indirectResult, intermediary);
                }
                else
                {
                    _messagingInstruments?.OnPingReplyMissed(TargetSiloAddress);

                    if (indirectResult.IntermediaryHealthScore > 0)
                    {
                        LogInformationIgnoringFailureResultForPing(_log, id, TargetSiloAddress, indirectResult.IntermediaryHealthScore);
                        probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, indirectResult, intermediary);
                    }
                    else
                    {
                        LogWarningIndirectProbeFailed(_log, id, TargetSiloAddress, intermediary, elapsed, indirectResult.ProbeResponseTime, indirectResult.FailureMessage, indirectResult.IntermediaryHealthScore);

                        var missed = ++_failedProbes;
                        probeResult = ProbeResult.CreateIndirect(missed, ProbeResultStatus.Failed, indirectResult, intermediary);
                    }
                }
            }
            catch (Exception exception)
            {
                _messagingInstruments?.OnPingReplyMissed(TargetSiloAddress);
                LogWarningIndirectProbeRequestFailed(_log, exception);
                probeResult = ProbeResult.CreateIndirect(_failedProbes, ProbeResultStatus.Unknown, default, intermediary);
            }

            return probeResult;
        }

        /// <inheritdoc />
        public bool CheckHealth(DateTime lastCheckTime, [MaybeNullWhen(true)] out string reason) => _pingTimer.CheckHealth(lastCheckTime, out reason);

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

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Going to send Ping #{Id} to probe silo {Silo}"
        )]
        private static partial void LogTraceGoingToSendPing(ILogger logger, int id, SiloAddress silo);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Got successful ping response for ping #{Id} from {Silo} with round trip time of {RoundTripTime}"
        )]
        private static partial void LogTraceGotSuccessfulPingResponse(ILogger logger, int id, SiloAddress silo, TimeSpan roundTripTime);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.MembershipMissedPing,
            Message = "Did not get response for probe #{Id} to silo {Silo} after {Elapsed}. Current number of consecutive failed probes is {FailedProbeCount}"
        )]
        private static partial void LogWarningDidNotGetResponseForProbe(ILogger logger, Exception exception, int id, SiloAddress silo, TimeSpan elapsed, int failedProbeCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Going to send indirect ping #{Id} to probe silo {Silo} via {Intermediary}"
        )]
        private static partial void LogTraceGoingToSendIndirectPing(ILogger logger, int id, SiloAddress silo, SiloAddress intermediary);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Indirect probe request #{Id} to silo {SiloAddress} via silo {IntermediarySiloAddress} succeeded after {RoundTripTime} with a direct probe response time of {ProbeResponseTime}."
        )]
        private static partial void LogInformationIndirectProbeSucceeded(ILogger logger, int id, SiloAddress siloAddress, SiloAddress intermediarySiloAddress, TimeSpan roundTripTime, TimeSpan probeResponseTime);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Ignoring failure result for ping #{Id} from {Silo} since the intermediary used to probe the silo is not healthy. Intermediary health degradation score: {IntermediaryHealthScore}."
        )]
        private static partial void LogInformationIgnoringFailureResultForPing(ILogger logger, int id, SiloAddress silo, int intermediaryHealthScore);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Indirect probe request #{Id} to silo {SiloAddress} via silo {IntermediarySiloAddress} failed after {RoundTripTime} with a direct probe response time of {ProbeResponseTime}. Failure message: {FailureMessage}. Intermediary health score: {IntermediaryHealthScore}."
        )]
        private static partial void LogWarningIndirectProbeFailed(ILogger logger, int id, SiloAddress siloAddress, SiloAddress intermediarySiloAddress, TimeSpan roundTripTime, TimeSpan probeResponseTime, string? failureMessage, int intermediaryHealthScore);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Indirect probe request failed."
        )]
        private static partial void LogWarningIndirectProbeRequestFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Recusing unhealthy intermediary '{Intermediary}' and trying again with remaining nodes"
        )]
        private static partial void LogInformationRecusingUnhealthyIntermediary(ILogger logger, SiloAddress intermediary);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Exception monitoring silo {SiloAddress}"
        )]
        private static partial void LogErrorExceptionMonitoringSilo(ILogger logger, Exception exception, SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Probe #{Id} to silo {SiloAddress} failed after {Elapsed}, but local execution stalled for {StallDuration} during the probe. Treating as inconclusive. Consecutive failed probe count remains at {FailedProbeCount}."
        )]
        private static partial void LogWarningProbeFailureDuringLocalStall(ILogger logger, int id, SiloAddress siloAddress, TimeSpan elapsed, TimeSpan stallDuration, int failedProbeCount);
    }
}
