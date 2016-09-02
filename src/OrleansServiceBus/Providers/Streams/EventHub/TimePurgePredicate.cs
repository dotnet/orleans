
using System;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Determines if data should be purged based off time.
    /// </summary>
    public class TimePurgePredicate
    {
        private readonly TimeSpan minTimeInCache;
        private readonly TimeSpan maxRelativeMessageAge;

        /// <summary>
        /// Default time purge predicate never purges by time.
        /// </summary>
        public static readonly TimePurgePredicate Default = new TimePurgePredicate(TimeSpan.MinValue, TimeSpan.MaxValue);

        /// <summary>
        /// Contructor
        /// </summary>
        /// <param name="minTimeInCache">minimum time data should be keept in cache, unless purged due to data size.</param>
        /// <param name="maxRelativeMessageAge">maximum age of data to keep in the cache</param>
        public TimePurgePredicate(TimeSpan minTimeInCache, TimeSpan maxRelativeMessageAge)
        {
            this.minTimeInCache = minTimeInCache;
            this.maxRelativeMessageAge = maxRelativeMessageAge;
        }

        /// <summary>
        /// Checks to see if the message should be purged.
        /// Message should be purged if it has been in the queue longer than the minTimeInCache, but it's relative age is greater than maxRelativeMessageAge.
        /// </summary>
        /// <param name="timeInService">amount of time message has been in this service</param>
        /// <param name="relativeAge">Age of message relative to the most recent events read</param>
        /// <returns></returns>
        public bool ShouldPurgFromTime(TimeSpan timeInService, TimeSpan relativeAge)
        {
            // if time in cache exceeds the minimum and age of data is greater than max allowed, purge
            return timeInService > minTimeInCache && relativeAge > maxRelativeMessageAge;
        }
    }
}
