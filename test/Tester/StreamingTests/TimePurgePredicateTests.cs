
using System;
using Orleans.ServiceBus.Providers;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class TimePurgePredicateTests
    {
        private static readonly TimeSpan minTimeInCache = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan maxRelitiveAgeInCache = TimeSpan.FromMinutes(30);
        private static readonly TimePurgePredicate timePurge = new TimePurgePredicate(minTimeInCache, maxRelitiveAgeInCache);
        private static readonly DateTime cacheMaxEnqueTime = new DateTime(2000, 3, 12, 10, 13, 32);
        private static readonly DateTime nowUtc = DateTime.UtcNow;

        /// <summary>
        /// Message has not been in cache long enough to purge, and message age is not old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_NoPurgeThreshold_Tests()
        {
            DateTime messageEnqueTime = cacheMaxEnqueTime - maxRelitiveAgeInCache;
            DateTime timeRead = nowUtc - minTimeInCache;
            TimeSpan timeInService = nowUtc - timeRead;
            TimeSpan relativeAge = cacheMaxEnqueTime - messageEnqueTime;
            Assert.Equal(false, timePurge.ShouldPurgFromTime(timeInService, relativeAge));
        }

        /// <summary>
        /// Message has been in cache long enough to purge, and message age is old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_PurgeDataThreshold_Tests()
        {
            DateTime messageEnqueTime = cacheMaxEnqueTime - maxRelitiveAgeInCache - TimeSpan.FromTicks(1);
            DateTime timeRead = nowUtc - minTimeInCache - TimeSpan.FromTicks(1);
            TimeSpan timeInService = nowUtc - timeRead;
            TimeSpan relativeAge = cacheMaxEnqueTime - messageEnqueTime;
            Assert.Equal(true, timePurge.ShouldPurgFromTime(timeInService, relativeAge));
        }

        /// <summary>
        /// Message has been in cache long enough to purge, but message age is not old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_NoPurgeAgeThreshold_Tests()
        {
            DateTime messageEnqueTime = cacheMaxEnqueTime - maxRelitiveAgeInCache;
            DateTime timeRead = nowUtc - minTimeInCache - TimeSpan.FromTicks(1);
            TimeSpan timeInService = nowUtc - timeRead;
            TimeSpan relativeAge = cacheMaxEnqueTime - messageEnqueTime;
            Assert.Equal(false, timePurge.ShouldPurgFromTime(timeInService, relativeAge));
        }

        /// <summary>
        /// Message has not been in cache long enough to purge, but message age is old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_NoPurgeTimeInCacheThreshold_Tests()
        {
            DateTime messageEnqueTime = cacheMaxEnqueTime - maxRelitiveAgeInCache - TimeSpan.FromTicks(1);
            DateTime timeRead = nowUtc - minTimeInCache;
            TimeSpan timeInService = nowUtc - timeRead;
            TimeSpan relativeAge = cacheMaxEnqueTime - messageEnqueTime;
            Assert.Equal(false, timePurge.ShouldPurgFromTime(timeInService, relativeAge));
        }

        /// <summary>
        /// Default time purge should not purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_DefaultNeverPurges_Tests()
        {
            Assert.Equal(false, TimePurgePredicate.Default.ShouldPurgFromTime(TimeSpan.Zero, TimeSpan.MaxValue));
        }
    }
}
