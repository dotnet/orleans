/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading;


namespace Orleans.Runtime
{

    internal class Watchdog : AsynchAgent
    {
        private static readonly TimeSpan heartbeatPeriod = TimeSpan.FromMilliseconds(1000);
        private readonly TimeSpan healthCheckPeriod;
        private DateTime lastHeartbeat;
        private DateTime lastWatchdogCheck;
        private readonly List<IHealthCheckParticipant> participants;
        private readonly TraceLogger logger;
        private readonly CounterStatistic watchdogChecks;
        private CounterStatistic watchdogFailedChecks;

        public Watchdog(TimeSpan watchdogPeriod, List<IHealthCheckParticipant> watchables)
        {
            logger = TraceLogger.GetLogger("Watchdog");
            healthCheckPeriod = watchdogPeriod;
            participants = watchables;
            watchdogChecks = CounterStatistic.FindOrCreate(StatisticNames.WATCHDOG_NUM_HEALTH_CHECKS);
        }

        public override void Start()
        {
            logger.Info("Starting Silo Watchdog.");
            var now = DateTime.UtcNow;
            lastHeartbeat = now;
            lastWatchdogCheck = now;
            base.Start();
        }

        #region Overrides of AsynchAgent

        protected override void Run()
        {
            while (!Cts.IsCancellationRequested)
            {
                try
                {
                    WatchdogHeartbeatTick(null);
                    Thread.Sleep(heartbeatPeriod);
                }
                catch (ThreadAbortException)
                {
                    // Silo is probably shutting-down, so just ignore and exit
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.Watchdog_InternalError, "Watchdog Internal Error.", exc);
                }
            }
        }

        #endregion

        private void WatchdogHeartbeatTick(object state)
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
            foreach (IHealthCheckParticipant participant in participants)
            {
                try
                {
                    bool ok = participant.CheckHealth(lastWatchdogCheck);
                    if (!ok)
                        numFailedChecks++;
                }
                catch (Exception exc) 
                {
                    logger.Warn(ErrorCode.Watchdog_ParticipantThrownException, 
                        String.Format("HealthCheckParticipant {0} has thrown an exception from its CheckHealth method.", participant.ToString()), exc); 
                }
            }
            if (numFailedChecks > 0)
            {
                if (watchdogFailedChecks == null)
                    watchdogFailedChecks = CounterStatistic.FindOrCreate(StatisticNames.WATCHDOG_NUM_FAILED_HEALTH_CHECKS);
                
                watchdogFailedChecks.Increment();
                logger.Warn(ErrorCode.Watchdog_HealthCheckFailure, String.Format("Watchdog had {0} Health Check Failure(s) out of {1} Health Check Participants.", numFailedChecks, participants.Count)); 
            }
            lastWatchdogCheck = DateTime.UtcNow;
        }

        private static void CheckYourOwnHealth(DateTime lastCheckt, TraceLogger logger)
        {
            var timeSinceLastTick = (DateTime.UtcNow - lastCheckt);
            if (timeSinceLastTick > heartbeatPeriod.Multiply(2))
            {
                var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                logger.Warn(ErrorCode.SiloHeartbeatTimerStalled,
                    ".NET Runtime Platform stalled for {0} - possibly GC? We are now using total of {1}MB memory. gc={2}, {3}, {4}",
                    timeSinceLastTick,
                    GC.GetTotalMemory(false) / (1024 * 1024),
                    gc[0],
                    gc[1],
                    gc[2]);
            }
        }
    }
}

