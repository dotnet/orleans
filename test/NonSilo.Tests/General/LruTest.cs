using System;
using System.Threading;
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
            LRU<string, string>.FetchValueDelegate f = null;

            var target = new LRU<string, string>(maxSize, maxAge, f);
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
            LRU<string, string>.FetchValueDelegate f = null;

            var target = new LRU<string, string>(maxSize, maxAge, f);
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
            LRU<string, string>.FetchValueDelegate f = null;

            var target = new LRU<string, string>(maxSize, maxAge, f);

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
                string val;
                target.TryGetValue(s, out val);
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
    }
}
