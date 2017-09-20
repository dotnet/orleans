using System;

namespace Orleans.Runtime.Counters
{
    /// <summary>
    /// Background publisher of counter values.
    /// Updates to counters needs to be very fast, so are all in-memory operations.
    /// This class then follows up to periodically write the counter values to OS
    /// </summary>
    internal class CountersStatistics
    {
        private static readonly Logger logger = LogManager.GetLogger("WindowsPerfCountersStatistics", LoggerType.Runtime);

        private const int ERROR_THRESHOLD = 10; // A totally arbitrary value!
        private readonly ITelemetryProducer telemetryProducer;
        private SafeTimer timer;
        private bool shouldWritePerfCounters = true;

        public TimeSpan PerfCountersWriteInterval { get; private set; }


        /// <summary>
        /// Initialize the counter publisher framework. Start the background stats writer thread.
        /// </summary>
        /// <param name="writeInterval">Frequency of writing to Windows perf counters</param>
        /// <param name="telemetryProducer">The metrics writer.</param>
        public CountersStatistics(TimeSpan writeInterval, ITelemetryProducer telemetryProducer)
        {
            if (writeInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(writeInterval), "Creating CounterStatsPublisher with negative or zero writeInterval");
            if (telemetryProducer == null) throw new ArgumentNullException(nameof(telemetryProducer));
            
            PerfCountersWriteInterval = writeInterval;
            this.telemetryProducer = telemetryProducer;
        }

        /// <summary>
        /// Start stats collection
        /// </summary>
        public void Start()
        {
            logger.Info(ErrorCode.PerfCounterStarting, "Starting Windows perf counter stats collection with frequency={0}", PerfCountersWriteInterval);

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
                int numErrors = OrleansCounterManager.WriteCounters(this.telemetryProducer);

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
