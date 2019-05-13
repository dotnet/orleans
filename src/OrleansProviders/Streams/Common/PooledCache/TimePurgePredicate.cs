
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
        /// Constructor
        /// </summary>
        /// <param name="minTimeInCache">minimum time data should be kept in cache, unless purged due to data size.</param>
        /// <param name="maxRelativeMessageAge">maximum age of data to keep in the cache</param>
        public TimePurgePredicate(TimeSpan minTimeInCache, TimeSpan maxRelativeMessageAge)
        {
            this.minTimeInCache = minTimeInCache;
            this.maxRelativeMessageAge = maxRelativeMessageAge;
        }

        /// <summary>
        /// Checks to see if the message should be purged.
        /// Message should be purged if its relative age is greater than maxRelativeMessageAge and has been in the cache longer than the minTimeInCache.
        /// </summary>
        /// <param name="timeInCache">amount of time message has been in this cache</param>
        /// <param name="relativeAge">Age of message relative to the most recent events read</param>
        /// <returns></returns>
        public virtual bool ShouldPurgFromTime(TimeSpan timeInCache, TimeSpan relativeAge)
        {
            // if time in cache exceeds the minimum and age of data is greater than max allowed, purge
            return timeInCache > minTimeInCache && relativeAge > maxRelativeMessageAge;
        }
    }
}
