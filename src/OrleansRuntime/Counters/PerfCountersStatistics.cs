using System;

namespace Orleans.Runtime.Counters
{
    /// <summary>
    /// Background publisher of counter values.
    /// Updates to counters needs to be very fast, so are all in-memory operations.
    /// This class then follows up to periodically write the counter values to OS
    /// </summary>
    internal class PerfCountersStatistics
    {
        private static readonly TraceLogger logger = TraceLogger.GetLogger("WindowsPerfCountersStatistics", TraceLogger.LoggerType.Runtime);

        private const int ERROR_THRESHOLD = 10; // A totally arbitrary value!
        private SafeTimer timer;
        private bool shouldWritePerfCounters = true;
        
        private const string CounterControlProgName = "OrleansCounterControl.exe";
        private const string ExplainHowToCreateOrleansPerfCounters = "Run " + CounterControlProgName + " as Administrator to create perf counters for Orleans.";
        
        public TimeSpan PerfCountersWriteInterval { get; private set; }

        
        /// <summary>
        /// Initialize the counter publisher framework. Start the background stats writer thread.
        /// </summary>
        /// <param name="writeInterval">Frequency of writing to Windows perf counters</param>
        public PerfCountersStatistics(TimeSpan writeInterval)
        {
            if (writeInterval <= TimeSpan.Zero)
                throw new ArgumentException("Creating CounterStatsPublisher with negative or zero writeInterval", "writeInterval");

            PerfCountersWriteInterval = writeInterval;
        }

        /// <summary>
        /// Prepare for stats collection
        /// </summary>
        private void Prepare()
        {
            if (Environment.OSVersion.ToString().StartsWith("unix", StringComparison.InvariantCultureIgnoreCase))
            {
                logger.Warn(ErrorCode.PerfCounterNotFound, "Windows perf counters are only available on Windows :) -- defaulting to in-memory counters.");
                shouldWritePerfCounters = false;
                return;
            }

            if (!OrleansPerfCounterManager.AreWindowsPerfCountersAvailable())
            {
                logger.Warn(ErrorCode.PerfCounterNotFound, "Windows perf counters not found -- defaulting to in-memory counters. " + ExplainHowToCreateOrleansPerfCounters);
                shouldWritePerfCounters = false;
                return;
            }
            
            try
            {
                OrleansPerfCounterManager.PrecreateCounters();
            }
            catch(Exception exc)
            {
                logger.Warn(ErrorCode.PerfCounterFailedToInitialize, "Failed to initialize perf counters -- defaulting to in-memory counters. " + ExplainHowToCreateOrleansPerfCounters, exc);
                shouldWritePerfCounters = false;
            }
        }

        /// <summary>
        /// Start stats collection
        /// </summary>
        public void Start()
        {
            logger.Info(ErrorCode.PerfCounterStarting, "Starting Windows perf counter stats collection with frequency={0}", PerfCountersWriteInterval);
            Prepare();
            // Start the timer
            timer = new SafeTimer(TimerTick, null, PerfCountersWriteInterval, PerfCountersWriteInterval);
        }

        /// <summary>
        /// Stop stats collection
        /// </summary>
        public void Stop()
        {
            logger.Info(ErrorCode.PerfCounterStopping, "Stopping  Windows perf counter stats collection");
            if (timer != null)
                timer.Dispose(); // Stop timer

            timer = null;
        }

        /// <summary>
        /// Handle a timer tick
        /// </summary>
        /// <param name="state"></param>
        private void TimerTick(object state)
        {
            if (shouldWritePerfCounters)
            {
                // Write counters to Windows perf counters
                int numErrors = OrleansPerfCounterManager.WriteCounters();

                if (numErrors > 0)
                {
                    logger.Warn(ErrorCode.PerfCounterWriteErrors,
                                "Completed writing Windows perf counters with {0} errors", numErrors);
                }
                else if (logger.IsVerbose2)
                {
                    logger.Verbose2(ErrorCode.PerfCounterWriteSuccess,
                                    "Completed writing Windows perf counters sucessfully");
                }

                if (numErrors > ERROR_THRESHOLD)
                {
                    logger.Error(ErrorCode.PerfCounterWriteTooManyErrors,
                                "Too many errors writing Windows perf counters -- disconnecting counters");
                    shouldWritePerfCounters = false;
                }
            }
            else if (logger.IsVerbose2)
            {
                logger.Verbose2("Skipping - Writing Windows perf counters is disabled");
            }
        }
    }
}
