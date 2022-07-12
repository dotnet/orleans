using System;
using System.Diagnostics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Non-allocating stopwatch for timing durations.
    /// </summary>
    internal struct ValueStopwatch
    {
        private static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double) Stopwatch.Frequency;
        private long value;

        /// <summary>
        /// Starts a new instance.
        /// </summary>
        /// <returns>A new, running stopwatch.</returns>
        public static ValueStopwatch StartNew() => new(GetTimestamp());

        /// <summary>
        /// Starts a new instance with an initial elapsed duration.
        /// </summary>
        /// <param name="elapsed">
        /// The initial elapsed duration.
        /// </param>
        /// <returns>A new, running stopwatch.</returns>
        public static ValueStopwatch StartNew(TimeSpan elapsed) => new(GetTimestamp() - (long)(elapsed.TotalSeconds * Stopwatch.Frequency));
        
        private ValueStopwatch(long timestamp)
        {
            this.value = timestamp;
        }
        
        /// <summary>
        /// Returns true if this instance is running or false otherwise.
        /// </summary>
        public bool IsRunning => this.value > 0;
        
        /// <summary>
        /// Returns the elapsed time.
        /// </summary>
        public TimeSpan Elapsed => TimeSpan.FromTicks(this.ElapsedTicks);

        /// <summary>
        /// Returns the elapsed ticks.
        /// </summary>
        public long ElapsedTicks
        {
            get
            {
                // A positive timestamp value indicates the start time of a running stopwatch,
                // a negative value indicates the negative total duration of a stopped stopwatch.
                var timestamp = this.value;
                
                long delta;
                if (this.IsRunning)
                {
                    // The stopwatch is still running.
                    var start = timestamp;
                    var end = Stopwatch.GetTimestamp();
                    delta = end - start;
                }
                else
                {
                    // The stopwatch has been stopped.
                    delta = -timestamp;
                }

                return (long) (delta * TimestampToTicks);
            }
        }

        /// <summary>
        /// Gets the number of ticks in the timer mechanism.
        /// </summary>
        /// <returns>The number of ticks in the timer mechanism</returns>
        public static long GetTimestamp() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Returns a new, stopped <see cref="ValueStopwatch"/> with the provided start and end timestamps.
        /// </summary>
        /// <param name="start">The start timestamp.</param>
        /// <param name="end">The end timestamp.</param>
        /// <returns>A new, stopped <see cref="ValueStopwatch"/> with the provided start and end timestamps.</returns>
        public static ValueStopwatch FromTimestamp(long start, long end) => new ValueStopwatch(-(end - start));

        /// <summary>
        /// Gets the raw counter value for this instance.
        /// </summary>
        /// <remarks> 
        /// A positive timestamp value indicates the start time of a running stopwatch,
        /// a negative value indicates the negative total duration of a stopped stopwatch.
        /// </remarks>
        /// <returns>The raw counter value.</returns>
        public long GetRawTimestamp() => this.value;

        /// <summary>
        /// Starts the stopwatch.
        /// </summary>
        public void Start()
        {
            var timestamp = this.value;
            
            // If already started, do nothing.
            if (this.IsRunning) return;

            // Stopwatch is stopped, therefore value is zero or negative.
            // Add the negative value to the current timestamp to start the stopwatch again.
            var newValue = GetTimestamp() + timestamp;
            if (newValue == 0) newValue = 1;
            this.value = newValue;
        }

        /// <summary>
        /// Restarts this stopwatch, beginning from zero time elapsed.
        /// </summary>
        public void Restart() => this.value = GetTimestamp();

        /// <summary>
        /// Resets this stopwatch into a stopped state with no elapsed duration.
        /// </summary>
        public void Reset() => this.value = 0;

        /// <summary>
        /// Stops this stopwatch.
        /// </summary>
        public void Stop()
        {
            var timestamp = this.value;

            // If already stopped, do nothing.
            if (!this.IsRunning) return;

            var end = GetTimestamp();
            var delta = end - timestamp;

            this.value = -delta;
        }
    }
}