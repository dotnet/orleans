using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.Runtime
{
    /// <summary>
    /// Monitors runtime and component health periodically, reporting complaints.
    /// </summary>
    internal partial class Watchdog(
        IOptions<ClusterMembershipOptions> clusterMembershipOptions,
        IEnumerable<IHealthCheckParticipant> participants,
        ILogger<Watchdog> logger,
        OrleansInstruments orleansInstruments,
        ILocalSiloHealthEventRecorder healthEventRecorder,
        [FromKeyedServices(TimeProviderNames.Membership)] TimeProvider timeProvider) : IDisposable
    {
        private static readonly TimeSpan PlatformWatchdogHeartbeatPeriod = TimeSpan.FromMilliseconds(1000);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TimeSpan _componentHealthCheckPeriod = clusterMembershipOptions.Value.LocalHealthDegradationMonitoringPeriod;
        private readonly List<IHealthCheckParticipant> _participants = participants.ToList();
        private readonly ILogger _logger = logger;
        private readonly WatchdogInstruments _watchdogInstruments = new(orleansInstruments);
        private readonly ILocalSiloHealthEventRecorder _healthEventRecorder = healthEventRecorder;
        private readonly TimeProvider _timeProvider = timeProvider;
        private long _platformWatchdogTimestamp;
        private long _componentWatchdogTimestamp;

        // GC pause duration since process start.
        private TimeSpan _cumulativeGCPauseDuration;

        private DateTime _lastComponentHealthCheckTime;
        private Thread? _platformWatchdogThread;
        private Thread? _componentWatchdogThread;

        public void Start()
        {
            LogDebugStartingSiloWatchdog(_logger);

            if (_platformWatchdogThread is not null)
            {
                throw new InvalidOperationException("Watchdog.Start may not be called more than once");
            }

            _platformWatchdogTimestamp = _timeProvider.GetTimestamp();
            _cumulativeGCPauseDuration = GC.GetTotalPauseDuration();

            _platformWatchdogThread = new Thread(RunPlatformWatchdog)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog.Platform",
            };
            _platformWatchdogThread.Start();

            _componentWatchdogTimestamp = _timeProvider.GetTimestamp();
            _lastComponentHealthCheckTime = _timeProvider.GetUtcNow().UtcDateTime;

            _componentWatchdogThread = new Thread(RunComponentWatchdog)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog.Component",
            };

            _componentWatchdogThread.Start();

            LogDebugSiloWatchdogStartedSuccessfully(_logger);
        }

        public void Stop()
        {
            Dispose();
        }

        protected void RunPlatformWatchdog()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    CheckRuntimeHealth();
                }
                catch (Exception exc)
                {
                    LogErrorPlatformWatchdogInternalError(_logger, exc);
                }

                _platformWatchdogTimestamp = _timeProvider.GetTimestamp();
                _cumulativeGCPauseDuration = GC.GetTotalPauseDuration();
                _cancellation.Token.WaitHandle.WaitOne(PlatformWatchdogHeartbeatPeriod);
            }
        }

        private void CheckRuntimeHealth()
        {
            if (Debugger.IsAttached)
            {
                return;
            }

            var pauseDurationSinceLastTick = GC.GetTotalPauseDuration() - _cumulativeGCPauseDuration;
            var timeSinceLastTick = _timeProvider.GetElapsedTime(_platformWatchdogTimestamp);
            if (pauseDurationSinceLastTick > TimeSpan.Zero)
            {
                _healthEventRecorder.RecordHealthEvent(
                    LocalSiloHealthCheckKind.GarbageCollectionPause,
                    score: 0,
                    complaint: $"The .NET runtime paused for garbage collection for {pauseDurationSinceLastTick}.",
                    duration: pauseDurationSinceLastTick);
            }

            if (timeSinceLastTick > PlatformWatchdogHeartbeatPeriod.Multiply(2))
            {
                var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                _healthEventRecorder.RecordHealthEvent(
                    LocalSiloHealthCheckKind.RuntimeStall,
                    score: 1,
                    complaint: $".NET Runtime Platform stalled for {timeSinceLastTick}. Memory: {GC.GetTotalMemory(false) / (1024 * 1024)}MB. Collection counts: 0: {gc[0]}, 1: {gc[1]}, 2: {gc[2]}.",
                    duration: timeSinceLastTick);
                LogWarningSiloHeartbeatTimerStalled(_logger, timeSinceLastTick, pauseDurationSinceLastTick, GC.GetTotalMemory(false) / (1024 * 1024), gc[0], gc[1], gc[2]);
            }

            var timeSinceLastParticipantCheck = _timeProvider.GetElapsedTime(_componentWatchdogTimestamp);
            if (timeSinceLastParticipantCheck > _componentHealthCheckPeriod.Multiply(2))
            {
                _healthEventRecorder.RecordHealthEvent(
                    LocalSiloHealthCheckKind.ComponentHealthCheckStall,
                    score: 1,
                    complaint: $"Participant check thread has not completed for {timeSinceLastParticipantCheck}.",
                    duration: timeSinceLastParticipantCheck);
                LogWarningParticipantCheckThreadStalled(_logger, timeSinceLastParticipantCheck);
            }
        }

        protected void RunComponentWatchdog()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    CheckComponentHealth();
                }
                catch (Exception exc)
                {
                    LogErrorComponentHealthCheckInternalError(_logger, exc);
                }

                _componentWatchdogTimestamp = _timeProvider.GetTimestamp();
                _cancellation.Token.WaitHandle.WaitOne(_componentHealthCheckPeriod);
            }
        }

        private void CheckComponentHealth()
        {
            _watchdogInstruments.OnHealthCheck();
            var numFailedChecks = 0;
            StringBuilder? complaints = null;

            // Restart the timer before the check to reduce false positives for the stall checker.
            _componentWatchdogTimestamp = _timeProvider.GetTimestamp();
            foreach (var participant in _participants)
            {
                try
                {
                    var ok = participant.CheckHealth(_lastComponentHealthCheckTime, out var complaint);
                    if (!ok)
                    {
                        complaints ??= new StringBuilder();
                        if (complaints.Length > 0)
                        {
                            complaints.Append(' ');
                        }

                        complaints.Append($"{participant.GetType()} failed health check with complaint \"{complaint}\".");
                        ++numFailedChecks;
                    }
                }
                catch (Exception exc)
                {
                    LogWarningHealthCheckParticipantException(_logger, exc, participant.GetType());
                }
            }

            if (complaints != null)
            {
                _watchdogInstruments.OnFailedHealthCheck();
                LogWarningHealthCheckFailure(_logger, numFailedChecks, _participants.Count, complaints);
            }

            _lastComponentHealthCheckTime = _timeProvider.GetUtcNow().UtcDateTime;
        }

        public void Dispose()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                _componentWatchdogThread?.Join();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                _platformWatchdogThread?.Join();
            }
            catch
            {
                // Ignore.
            }
        }

        private readonly struct ParticipantTypeLogValue(Type participantType)
        {
            public override string ToString() => participantType.ToString();
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting silo watchdog"
        )]
        private static partial void LogDebugStartingSiloWatchdog(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo watchdog started successfully."
        )]
        private static partial void LogDebugSiloWatchdogStartedSuccessfully(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.Watchdog_InternalError,
            Level = LogLevel.Error,
            Message = "Platform watchdog encountered an internal error"
        )]
        private static partial void LogErrorPlatformWatchdogInternalError(ILogger logger, Exception exc);

        [LoggerMessage(
            EventId = (int)ErrorCode.SiloHeartbeatTimerStalled,
            Level = LogLevel.Warning,
            Message = ".NET Runtime Platform stalled for {TimeSinceLastTick}. Total GC Pause duration during that period: {PauseDurationSinceLastTick}. We are now using a total of {TotalMemory}MB memory. Collection counts per generation: 0: {GCGen0Count}, 1: {GCGen1Count}, 2: {GCGen2Count}"
        )]
        private static partial void LogWarningSiloHeartbeatTimerStalled(ILogger logger, TimeSpan timeSinceLastTick, TimeSpan pauseDurationSinceLastTick, long totalMemory, int gcGen0Count, int gcGen1Count, int gcGen2Count);

        [LoggerMessage(
            EventId = (int)ErrorCode.SiloHeartbeatTimerStalled,
            Level = LogLevel.Warning,
            Message = "Participant check thread has not completed for {TimeSinceLastTick}, potentially indicating lock contention or deadlock, CPU starvation, or another execution anomaly."
        )]
        private static partial void LogWarningParticipantCheckThreadStalled(ILogger logger, TimeSpan timeSinceLastTick);

        [LoggerMessage(
            EventId = (int)ErrorCode.Watchdog_InternalError,
            Level = LogLevel.Error,
            Message = "Component health check encountered an internal error"
        )]
        private static partial void LogErrorComponentHealthCheckInternalError(ILogger logger, Exception exc);

        [LoggerMessage(
            EventId = (int)ErrorCode.Watchdog_ParticipantThrownException,
            Level = LogLevel.Warning,
            Message = "Health check participant {Participant} has thrown an exception from its CheckHealth method."
        )]
        private static partial void LogWarningHealthCheckParticipantException(ILogger logger, Exception exc, Type participant);

        [LoggerMessage(
            EventId = (int)ErrorCode.Watchdog_HealthCheckFailure,
            Level = LogLevel.Warning,
            Message = "{FailedChecks} of {ParticipantCount} components reported issues. Complaints: {Complaints}"
        )]
        private static partial void LogWarningHealthCheckFailure(ILogger logger, int failedChecks, int participantCount, StringBuilder complaints);
    }
}
