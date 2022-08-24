using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Cheap, non-allocating stopwatch for timing durations with an accuracy within tens of milliseconds.
    /// </summary>
    internal struct CoarseStopwatch
    {
        private long _value;

        /// <summary>
        /// Starts a new instance.
        /// </summary>
        /// <returns>A new, running stopwatch.</returns>
        public static CoarseStopwatch StartNew() => new(GetTimestamp());

        /// <summary>
        /// Starts a new instance with the specified duration already elapsed.
        /// </summary>
        /// <returns>A new, running stopwatch.</returns>
        public static CoarseStopwatch StartNew(long elapsedMs) => new(GetTimestamp() - elapsedMs);

        /// <summary>
        /// Creates a new instance with the specified timestamp.
        /// </summary>
        /// <returns>A new stopwatch.</returns>
        public static CoarseStopwatch FromTimestamp(long timestamp) => new(timestamp);
        
        private CoarseStopwatch(long timestamp)
        {
            _value = timestamp;
        }

        /// <summary>
        /// The number of ticks per second for this stopwatch.
        /// </summary>
        public const long Frequency = 1000;

        /// <summary>
        /// Returns true if this instance is running or false otherwise.
        /// </summary>
        public bool IsRunning => _value > 0;
        
        /// <summary>
        /// Returns the elapsed time.
        /// </summary>
        public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMilliseconds);

        /// <summary>
        /// Returns a value indicating whether this instance has the default value.
        /// </summary>
        public bool IsDefault => _value == 0;

        /// <summary>
        /// Returns the elapsed ticks.
        /// </summary>
        public long ElapsedMilliseconds
        {
            get
            {
                // A positive timestamp value indicates the start time of a running stopwatch,
                // a negative value indicates the negative total duration of a stopped stopwatch.
                var timestamp = _value;
                
                long delta;
                if (IsRunning)
                {
                    // The stopwatch is still running.
                    var start = timestamp;
                    var end = GetTimestamp();
                    delta = end - start;
                }
                else
                {
                    // The stopwatch has been stopped.
                    delta = -timestamp;
                }

                return delta;
            }
        }

        /// <summary>
        /// Gets the number of ticks in the timer mechanism.
        /// </summary>
        /// <returns>The number of ticks in the timer mechanism</returns>
        public static long GetTimestamp() => Environment.TickCount64;

        /// <summary>
        /// Returns a new, stopped <see cref="CoarseStopwatch"/> with the provided start and end timestamps.
        /// </summary>
        /// <param name="start">The start timestamp.</param>
        /// <param name="end">The end timestamp.</param>
        /// <returns>A new, stopped <see cref="CoarseStopwatch"/> with the provided start and end timestamps.</returns>
        public static CoarseStopwatch FromTimestamp(long start, long end) => new(-(end - start));

        /// <summary>
        /// Gets the raw counter value for this instance.
        /// </summary>
        /// <remarks> 
        /// A positive timestamp value indicates the start time of a running stopwatch,
        /// a negative value indicates the negative total duration of a stopped stopwatch.
        /// </remarks>
        /// <returns>The raw counter value.</returns>
        public long GetRawTimestamp() => _value;

        /// <summary>
        /// Starts the stopwatch.
        /// </summary>
        public void Start()
        {
            var timestamp = _value;
            
            // If already started, do nothing.
            if (IsRunning) return;

            // Stopwatch is stopped, therefore value is zero or negative.
            // Add the negative value to the current timestamp to start the stopwatch again.
            var newValue = GetTimestamp() + timestamp;
            if (newValue == 0) newValue = 1;
            _value = newValue;
        }

        /// <summary>
        /// Restarts this stopwatch, beginning from zero time elapsed.
        /// </summary>
        public void Restart() => _value = GetTimestamp();

        /// <summary>
        /// Resets this stopwatch into a stopped state with no elapsed duration.
        /// </summary>
        public void Reset() => _value = 0;

        /// <summary>
        /// Stops this stopwatch.
        /// </summary>
        public void Stop()
        {
            var timestamp = _value;

            // If already stopped, do nothing.
            if (!IsRunning) return;

            var end = GetTimestamp();
            var delta = end - timestamp;

            _value = -delta;
        }

        public override bool Equals(object obj) => obj is CoarseStopwatch stopwatch && _value == stopwatch._value;
        public bool Equals(CoarseStopwatch other) => _value == other._value;
        public override int GetHashCode() => HashCode.Combine(_value);
        public static bool operator== (CoarseStopwatch left, CoarseStopwatch right) => left.Equals(right);
        public static bool operator!= (CoarseStopwatch left, CoarseStopwatch right) => !left.Equals(right);
    }
}
