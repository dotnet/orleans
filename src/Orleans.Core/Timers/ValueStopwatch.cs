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
        public static ValueStopwatch StartNew() => new ValueStopwatch(Stopwatch.GetTimestamp());
        
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
            var newValue = Stopwatch.GetTimestamp() + timestamp;
            if (newValue == 0) newValue = 1;
            this.value = newValue;
        }

        /// <summary>
        /// Restarts this stopwatch, begining from zero time elapsed.
        /// </summary>
        public void Restart() => this.value = Stopwatch.GetTimestamp();

        /// <summary>
        /// Stops this stopwatch.
        /// </summary>
        public void Stop()
        {
            var timestamp = this.value;

            // If already stopped, do nothing.
            if (!this.IsRunning) return;

            var end = Stopwatch.GetTimestamp();
            var delta = end - timestamp;

            this.value = -delta;
        }
    }
}