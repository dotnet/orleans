using Orleans.Providers.Streams.Common;
using Xunit;

namespace ServiceBus.Tests.StreamingTests
{
    public class TimePurgePredicateTests
    {
        private static readonly TimeSpan MinTimeInCache = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxRelitiveAgeInCache = TimeSpan.FromMinutes(30);
        private static readonly TimePurgePredicate TimePurge = new TimePurgePredicate(MinTimeInCache, MaxRelitiveAgeInCache);
        private static readonly DateTime CacheMaxEnqueTime = new DateTime(2000, 3, 12, 10, 13, 32, DateTimeKind.Utc);
        private static readonly DateTime NowUtc = DateTime.UtcNow;

        /// <summary>
        /// Message has not been in cache long enough to purge, and message age is not old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_NoPurgeThreshold_Tests()
        {
            DateTime messageEnqueTime = CacheMaxEnqueTime - MaxRelitiveAgeInCache;
            DateTime timeRead = NowUtc - MinTimeInCache;
            TimeSpan timeInCache = NowUtc - timeRead;
            TimeSpan relativeAge = CacheMaxEnqueTime - messageEnqueTime;
            Assert.False(TimePurge.ShouldPurgeFromTime(timeInCache, relativeAge));
        }

        /// <summary>
        /// Message has been in cache long enough to purge, and message age is old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_PurgeDataThreshold_Tests()
        {
            DateTime messageEnqueTime = CacheMaxEnqueTime - MaxRelitiveAgeInCache - TimeSpan.FromTicks(1);
            DateTime timeRead = NowUtc - MinTimeInCache - TimeSpan.FromTicks(1);
            TimeSpan timeInCache = NowUtc - timeRead;
            TimeSpan relativeAge = CacheMaxEnqueTime - messageEnqueTime;
            Assert.True(TimePurge.ShouldPurgeFromTime(timeInCache, relativeAge));
        }

        /// <summary>
        /// Message has been in cache long enough to purge, but message age is not old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_NoPurgeAgeThreshold_Tests()
        {
            DateTime messageEnqueTime = CacheMaxEnqueTime - MaxRelitiveAgeInCache;
            DateTime timeRead = NowUtc - MinTimeInCache - TimeSpan.FromTicks(1);
            TimeSpan timeInCache = NowUtc - timeRead;
            TimeSpan relativeAge = CacheMaxEnqueTime - messageEnqueTime;
            Assert.False(TimePurge.ShouldPurgeFromTime(timeInCache, relativeAge));
        }

        /// <summary>
        /// Message has not been in cache long enough to purge, but message age is old enough to purge
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void TimePurgePredicate_NoPurgeTimeInCacheThreshold_Tests()
        {
            DateTime messageEnqueTime = CacheMaxEnqueTime - MaxRelitiveAgeInCache - TimeSpan.FromTicks(1);
            DateTime timeRead = NowUtc - MinTimeInCache;
            TimeSpan timeInCache = NowUtc - timeRead;
            TimeSpan relativeAge = CacheMaxEnqueTime - messageEnqueTime;
            Assert.False(TimePurge.ShouldPurgeFromTime(timeInCache, relativeAge));
        }
    }
}
