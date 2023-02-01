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
        private static readonly TimeSpan heartbeatPeriod = TimeSpan.FromMilliseconds(1000);
        private readonly TimeSpan healthCheckPeriod;
        private DateTime lastHeartbeat;
        private DateTime lastWatchdogCheck;

        // GC pause duration since process start.
        private TimeSpan cumulativeGCPauseDuration;

        private readonly List<IHealthCheckParticipant> participants;
        private readonly ILogger logger;
        private Thread thread;

        public Watchdog(TimeSpan watchdogPeriod, List<IHealthCheckParticipant> watchables, ILogger<Watchdog> logger)
        {
            this.logger = logger;
            healthCheckPeriod = watchdogPeriod;
            participants = watchables;
        }

        public void Start()
        {
            logger.LogInformation("Starting Silo Watchdog.");

            if (thread is not null)
            {
                throw new InvalidOperationException("Watchdog.Start may not be called more than once");
            }

            var now = DateTime.UtcNow;
            lastHeartbeat = now;
            lastWatchdogCheck = now;
            cumulativeGCPauseDuration = GC.GetTotalPauseDuration();

            this.thread = new Thread(this.Run)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog",
            };
            this.thread.Start();
        }

        public void Stop()
        {
            cancellation.Cancel();
        }

        protected void Run()
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                try
                {
                    WatchdogHeartbeatTick();
                    Thread.Sleep(heartbeatPeriod);
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

        private void WatchdogHeartbeatTick()
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
            if (timeSinceLastTick > heartbeatPeriod.Multiply(2))
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

