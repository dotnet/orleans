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
        private readonly List<IHealthCheckParticipant> participants;
        private readonly ILogger logger;
        private readonly CounterStatistic watchdogChecks;
        private CounterStatistic watchdogFailedChecks;
        private Thread thread;

        public Watchdog(TimeSpan watchdogPeriod, List<IHealthCheckParticipant> watchables, ILogger<Watchdog> logger)
        {
            this.logger = logger;
            healthCheckPeriod = watchdogPeriod;
            participants = watchables;
            watchdogChecks = CounterStatistic.FindOrCreate(StatisticNames.WATCHDOG_NUM_HEALTH_CHECKS);
        }

        public void Start()
        {
            logger.Info("Starting Silo Watchdog.");
            var now = DateTime.UtcNow;
            lastHeartbeat = now;
            lastWatchdogCheck = now;
            if (thread is object) throw new InvalidOperationException("Watchdog.Start may not be called more than once");
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
                CheckYourOwnHealth(lastHeartbeat, logger);
            }
            finally
            {
                lastHeartbeat = DateTime.UtcNow;
            }
            
            var timeSinceLastWatchdogCheck = (DateTime.UtcNow - lastWatchdogCheck);
            if (timeSinceLastWatchdogCheck <= healthCheckPeriod) return;

            watchdogChecks.Increment();
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
                            reasons.Append(" ");
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
                        participant?.GetType());
                }
            }
            if (numFailedChecks > 0)
            {
                if (watchdogFailedChecks == null)
                    watchdogFailedChecks = CounterStatistic.FindOrCreate(StatisticNames.WATCHDOG_NUM_FAILED_HEALTH_CHECKS);
                
                watchdogFailedChecks.Increment();
                logger.LogWarning((int)ErrorCode.Watchdog_HealthCheckFailure, "Watchdog had {FailedChecks} health Check failure(s) out of {ParticipantCount} health Check participants: {Reasons}", numFailedChecks, participants.Count, reasons.ToString()); 
            }
            lastWatchdogCheck = DateTime.UtcNow;
        }

        private static void CheckYourOwnHealth(DateTime lastCheckt, ILogger logger)
        {
            var timeSinceLastTick = (DateTime.UtcNow - lastCheckt);
            if (timeSinceLastTick > heartbeatPeriod.Multiply(2))
            {
                var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                logger.LogWarning(
                    (int)ErrorCode.SiloHeartbeatTimerStalled,
                    ".NET Runtime Platform stalled for {TimeSinceLastTick} - possibly GC? We are now using total of {TotalMemory}MB memory. gc={GCGen0Count}, {GCGen1Count}, {GCGen2Count}",
                    timeSinceLastTick,
                    GC.GetTotalMemory(false) / (1024 * 1024),
                    gc[0],
                    gc[1],
                    gc[2]);
            }
        }
    }
}

