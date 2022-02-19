
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Determines if data should be purged based off time.
    /// </summary>
    public class TimePurgePredicate
    {
        private readonly TimeSpan minTimeInCache;
        private readonly TimeSpan maxRelativeMessageAge;

        /// <summary>
        /// Initializes a new instance of the <see cref="TimePurgePredicate"/> class.
        /// </summary>
        /// <param name="minTimeInCache">The minimum time data should be kept in cache, unless purged due to data size.</param>
        /// <param name="maxRelativeMessageAge">The maximum age of data to keep in the cache.</param>
        public TimePurgePredicate(TimeSpan minTimeInCache, TimeSpan maxRelativeMessageAge)
        {
            this.minTimeInCache = minTimeInCache;
            this.maxRelativeMessageAge = maxRelativeMessageAge;
        }

        /// <summary>
        /// Checks to see if the message should be purged.
        /// Message should be purged if its relative age is greater than <c>maxRelativeMessageAge</c> and has been in the cache longer than the minTimeInCache.
        /// </summary>
        /// <param name="timeInCache">The amount of time message has been in this cache</param>
        /// <param name="relativeAge">The age of message relative to the most recent events read</param>
        /// <returns><see langword="true"/> if the message should be purged; otherwise <see langword="false"/>.</returns>
        public virtual bool ShouldPurgeFromTime(TimeSpan timeInCache, TimeSpan relativeAge)
        {
            // if time in cache exceeds the minimum and age of data is greater than max allowed, purge
            return timeInCache > minTimeInCache && relativeAge > maxRelativeMessageAge;
        }
    }
}
