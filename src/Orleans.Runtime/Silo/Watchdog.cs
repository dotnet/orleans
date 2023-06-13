using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class Watchdog
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private static readonly TimeSpan gcHeartbeatPeriod = TimeSpan.FromMilliseconds(1000);
        private readonly TimeSpan healthCheckPeriod;
        private DateTime lastHeartbeat;
        private DateTime lastWatchdogCheck;

        // GC pause duration since process start.
        private TimeSpan cumulativeGCPauseDuration;

        private readonly List<IHealthCheckParticipant> participants;
        private readonly ILogger logger;
        private Thread gcThread;
        private Thread participantsThread;

        public Watchdog(TimeSpan watchdogPeriod, List<IHealthCheckParticipant> watchables, ILogger<Watchdog> logger)
        {
            this.logger = logger;
            healthCheckPeriod = watchdogPeriod;
            participants = watchables;
        }

        public void Start()
        {
            logger.LogInformation("Starting Silo Watchdog.");

            if (gcThread is not null)
            {
                throw new InvalidOperationException("Watchdog.Start may not be called more than once");
            }

            var now = DateTime.UtcNow;
            lastHeartbeat = now;
            cumulativeGCPauseDuration = GC.GetTotalPauseDuration();

            this.gcThread = new Thread(this.RunGCCheck)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog.GCMonitor",
            };
            this.gcThread.Start();

            lastWatchdogCheck = now;

            this.participantsThread = new Thread(this.RunParticipantsCheck)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog.ParticipantsMonitor",
            };
            this.participantsThread.Start();
        }

        public void Stop()
        {
            cancellation.Cancel();
        }

        protected void RunGCCheck()
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                try
                {
                    GCWatchdogHeartbeatTick();
                    Thread.Sleep(gcHeartbeatPeriod);
                }
                catch (ThreadAbortException)
                {
                    // Silo is probably shutting-down, so just ignore and exit
                }
                catch (Exception exc)
                {
                    logger.LogError((int)ErrorCode.Watchdog_InternalError, exc, "Watchdog encountered an internal error");
                }
            }
        }

        protected void RunParticipantsCheck()
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                try
                {
                    ParticipantsWatchdogHeartbeatTick();
                    Thread.Sleep(healthCheckPeriod);
                }
                catch (ThreadAbortException)
                {
                    // Silo is probably shutting-down, so just ignore and exit
                }
                catch (Exception exc)
                {
                    logger.LogError((int)ErrorCode.Watchdog_InternalError, exc, "Watchdog encountered an internal error");
                }
            }
        }

        private void GCWatchdogHeartbeatTick()
        {
            try
            {
                CheckYourOwnHealth(lastHeartbeat, cumulativeGCPauseDuration, logger);
            }
            finally
            {
                lastHeartbeat = DateTime.UtcNow;
                cumulativeGCPauseDuration = GC.GetTotalPauseDuration();
            }
        }

        private void ParticipantsWatchdogHeartbeatTick()
        {
            var timeSinceLastWatchdogCheck = DateTime.UtcNow - lastWatchdogCheck;
            if (timeSinceLastWatchdogCheck <= healthCheckPeriod)
            {
                return;
            }

            WatchdogInstruments.HealthChecks.Add(1);
            int numFailedChecks = 0;
            StringBuilder reasons = null;
            foreach (IHealthCheckParticipant participant in participants)
            {
                try
                {
                    bool ok = participant.CheckHealth(lastWatchdogCheck, out var reason);
                    if (!ok)
                    {
                        reasons ??= new StringBuilder();
                        if (reasons.Length > 0)
                        {
                            reasons.Append(' ');
                        }

                        reasons.Append($"{participant.GetType()} failed health check with reason \"{reason}\".");
                        numFailedChecks++;
                    }
                }
                catch (Exception exc)
                {
                    logger.LogWarning(
                        (int)ErrorCode.Watchdog_ParticipantThrownException,
                        exc,
                        "Health check participant {Participant} has thrown an exception from its CheckHealth method.",
                        participant.GetType());
                }
            }

            if (numFailedChecks > 0)
            {
                WatchdogInstruments.FailedHealthChecks.Add(1);
                logger.LogWarning((int)ErrorCode.Watchdog_HealthCheckFailure, "Watchdog had {FailedChecks} health Check failure(s) out of {ParticipantCount} health Check participants: {Reasons}", numFailedChecks, participants.Count, reasons.ToString());
            }

            lastWatchdogCheck = DateTime.UtcNow;
        }

        private static void CheckYourOwnHealth(DateTime lastCheckTime, TimeSpan lastCumulativeGCPauseDuration, ILogger logger)
        {
            var timeSinceLastTick = DateTime.UtcNow - lastCheckTime;
            var pauseDurationSinceLastTick = GC.GetTotalPauseDuration() - lastCumulativeGCPauseDuration;
            if (timeSinceLastTick > gcHeartbeatPeriod.Multiply(2))
            {
                var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                logger.LogWarning(
                    (int)ErrorCode.SiloHeartbeatTimerStalled,
                    ".NET Runtime Platform stalled for {TimeSinceLastTick}. Total GC Pause duration during that period: {pauseDurationSinceLastTick}. We are now using a total of {TotalMemory}MB memory. gc={GCGen0Count}, {GCGen1Count}, {GCGen2Count}",
                    timeSinceLastTick,
                    pauseDurationSinceLastTick,
                    GC.GetTotalMemory(false) / (1024 * 1024),
                    gc[0],
                    gc[1],
                    gc[2]);
            }
        }
    }
}
