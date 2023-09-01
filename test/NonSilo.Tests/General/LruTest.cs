using Orleans.Runtime;
using Xunit;

namespace UnitTests
{
    /// <summary>
    ///This is a test class for the LRU class and is intended
    ///to contain all LRU Unit Tests
    ///</summary>
    public class LruTest
    {
        [Fact, TestCategory("BVT"), TestCategory("LRU")]
        public void LruCountTest()
        {
            const int maxSize = 10;
            var maxAge = new TimeSpan(0, 1, 0, 0);

            var target = new LRU<string, string>(maxSize, maxAge);
            Assert.Equal(0, target.Count);  // "Count wrong after construction"

            target.Add("1", "one");
            Assert.Equal(1, target.Count);  // "Count wrong after adding one item"
            
            target.Add("2", "two");
            Assert.Equal(2, target.Count);  // "Count wrong after adding two items"
        }

        [Fact, TestCategory("BVT"), TestCategory("LRU")]
        public void LruMaximumSizeTest()
        {
            const int maxSize = 10;
            var maxAge = new TimeSpan(0, 1, 0, 0);

            var target = new LRU<string, string>(maxSize, maxAge);
            for (var i = 1; i <= maxSize + 5; i++)
            {
                var s = i.ToString();
                target.Add(s, "item " + s);
                Thread.Sleep(10);                
            }

            Assert.Equal(maxSize, target.Count);  // "LRU grew larger than maximum size"
            for (var i = 1; i <= 5; i++)
            {
                var s = i.ToString();
                Assert.False(target.ContainsKey(s), "'Older' entry is still in cache");
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("LRU")]
        public void LruUsageTest()
        {
            const int maxSize = 10;
            var maxAge = new TimeSpan(0, 1, 0, 0);

            var target = new LRU<string, string>(maxSize, maxAge);

            // Fill the LRU with "1" through "10"
            for (var i = 1; i <= maxSize; i++)
            {
                var s = i.ToString();
                target.Add(s, "item " + s);
                Thread.Sleep(10);
            }

            // Use "10", then "9", etc.
            for (var i = maxSize; i >= 1; i--)
            {
                var s = i.ToString();
                target.TryGetValue(s, out _);
            }
            
            // Add a new item to push the least recently used out -- which should be item "10"
            var s1 = (maxSize + 1).ToString();
            target.Add(s1, "item " + s1);

            Assert.Equal(maxSize, target.Count);  // "Cache has exceeded maximum size"
            var s0 = maxSize.ToString();
            Assert.False(target.ContainsKey(s0), "Least recently used item was not expelled");
            for (var i = 1; i < maxSize; i++)
            {
                var s = i.ToString();
                Assert.True(target.ContainsKey(s), "Recently used item " + s + " was incorrectly expelled");
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("LRU")]
        public async Task LruRemoveExpired()
        {
            const int n = 10;
            const int maxSize = n*2;
            var maxAge = TimeSpan.FromMilliseconds(500);
            var flushCounter = 0;

            var target = new LRU<string, string>(maxSize, maxAge);
            target.RaiseFlushEvent += () => flushCounter++;

            for (int i = 0; i < n; i++)
            {
                var s = i.ToString();
                target.Add(s, $"item {s}");
            }

            target.RemoveExpired();
            Assert.Equal(0, flushCounter);
            Assert.Equal(n, target.Count);

            await Task.Delay(maxAge.Add(maxAge));

            target.Add("expected", "value");
            target.RemoveExpired();

            Assert.Equal(n, flushCounter);
            Assert.Equal(1, target.Count);
            Assert.True(target.TryGetValue("expected", out var value));
            Assert.Equal("value", value);
        }
    }
}
