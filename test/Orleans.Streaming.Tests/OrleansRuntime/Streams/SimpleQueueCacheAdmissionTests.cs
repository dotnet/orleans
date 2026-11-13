using System.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT")]
[TestCategory("Streaming")]
public class SimpleQueueCacheAdmissionTests
{
    [Theory]
    [InlineData(3, 1)]
    [InlineData(20, 18)]
    public void CapacityFailurePreservesExistingCacheAndSameListCanBeRetried(int capacity, int existingCount)
    {
        var cache = new SimpleQueueCache(capacity, NullLogger.Instance);
        var existing = Enumerable.Range(1, existingCount).Select(number => new TestBatch(number)).ToArray();
        cache.AddToCache(existing);
        var incoming = Enumerable.Range(existingCount + 1, 3).Select(number => new TestBatch(number)).ToArray();

        Assert.Throws<CacheFullException>(() => cache.AddToCache(incoming));
        Assert.Equal(existingCount, cache.Size);
        Assert.False(cache.IsUnderPressure());
        Assert.All(incoming, batch => Assert.Equal(0, batch.TokenReads));
        AssertAndDrain(cache, existing);

        cache.AddToCache(incoming);

        Assert.All(incoming, batch => Assert.Equal(1, batch.TokenReads));
        AssertAndDrain(cache, incoming);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidLateInputDoesNotAdmitAPartialPrefix(bool nullBatch)
    {
        var cache = new SimpleQueueCache(20, NullLogger.Instance);
        var existing = new TestBatch(1);
        cache.AddToCache([existing]);
        var first = new TestBatch(2);
        var invalid = new TestBatch(3) { Token = null! };
        IList<IBatchContainer> incoming = new IBatchContainer[] { first, nullBatch ? null! : invalid };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = Assert.Throws<ArgumentException>(() => cache.AddToCache(incoming));
            Assert.Equal("msgs", exception.ParamName);
            Assert.Equal(1, cache.Size);
            Assert.False(cache.IsUnderPressure());
        }

        invalid.Token = new EventSequenceTokenV2(3);
        incoming[1] = invalid;
        cache.AddToCache(incoming);

        AssertAndDrain(cache, existing, first, invalid);
    }

    [Fact]
    public void LateTokenGetterFailureLeavesBucketCountsUnchangedAndRetryReadsTokensOnce()
    {
        var cache = new SimpleQueueCache(20, NullLogger.Instance);
        var existing = new TestBatch(1);
        cache.AddToCache([existing]);
        var first = new TestBatch(2);
        var second = new TestBatch(3);
        var failing = new TestBatch(4) { FailNextTokenRead = true };
        IList<IBatchContainer> incoming = new IBatchContainer[] { first, second, failing };

        Assert.Throws<InvalidOperationException>(() => cache.AddToCache(incoming));
        Assert.Equal(1, cache.Size);
        Assert.False(cache.IsUnderPressure());

        cache.AddToCache(incoming);

        // No second provider-token access is made during the admission commit.
        Assert.Equal(2, first.TokenReads);
        Assert.Equal(2, second.TokenReads);
        Assert.Equal(2, failing.TokenReads);
        AssertAndDrain(cache, existing, first, second, failing);
    }

    [Fact]
    public void LateListAccessFailureDoesNotAdmitEarlierItems()
    {
        var cache = new SimpleQueueCache(20, NullLogger.Instance);
        var existing = new TestBatch(1);
        cache.AddToCache([existing]);
        var first = new TestBatch(2);
        var second = new TestBatch(3);
        var third = new TestBatch(4);
        var incoming = new ThrowingList([first, second, third]) { FailAtIndex = 2 };

        Assert.Throws<InvalidOperationException>(() => cache.AddToCache(incoming));
        Assert.Equal(1, cache.Size);
        cache.AddToCache(incoming);

        AssertAndDrain(cache, existing, first, second, third);
    }

    [Fact]
    public void LoggingFailureIsExplicitAndOccursBeforeCommit()
    {
        var logger = new ThrowingLogger();
        var cache = new SimpleQueueCache(20, logger);
        var existing = new TestBatch(1);
        cache.AddToCache([existing]);
        IList<IBatchContainer> incoming = new IBatchContainer[] { new TestBatch(2), new TestBatch(3) };
        logger.FailNextLog = true;

        Assert.Throws<InvalidOperationException>(() => cache.AddToCache(incoming));
        Assert.Equal(1, cache.Size);
        Assert.False(cache.IsUnderPressure());
        cache.AddToCache(incoming);

        AssertAndDrain(cache, existing, incoming[0], incoming[1]);
    }

    [Fact]
    public void EmptyAdmissionAtCapacityDoesNotChangeTheCache()
    {
        var cache = new SimpleQueueCache(1, NullLogger.Instance);
        var existing = new TestBatch(1);
        cache.AddToCache([existing]);

        cache.AddToCache(Array.Empty<IBatchContainer>());

        AssertAndDrain(cache, existing);
    }

    private static void AssertAndDrain(SimpleQueueCache cache, params IBatchContainer[] expected)
    {
        Assert.Equal(expected.Length, cache.Size);
        Assert.True(cache.TryPurgeFromCache(out var purged));
        Assert.Equal<IBatchContainer>(expected, purged);
        Assert.Equal(0, cache.Size);
        Assert.False(cache.IsUnderPressure());
    }

    private sealed class TestBatch(long number) : IBatchContainer
    {
        public StreamId StreamId => StreamId.Create("admission", "target");
        public StreamSequenceToken Token { get; set; } = new EventSequenceTokenV2(number);
        public bool FailNextTokenRead { get; set; }
        public int TokenReads { get; private set; }
        public StreamSequenceToken SequenceToken
        {
            get
            {
                TokenReads++;
                if (FailNextTokenRead)
                {
                    FailNextTokenRead = false;
                    throw new InvalidOperationException("Transient token getter failure.");
                }

                return Token;
            }
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
        public bool ImportRequestContext() => false;
    }

    private sealed class ThrowingList(List<IBatchContainer> items) : IList<IBatchContainer>
    {
        public int? FailAtIndex { get; set; }
        public IBatchContainer this[int index]
        {
            get
            {
                if (FailAtIndex == index)
                {
                    FailAtIndex = null;
                    throw new InvalidOperationException("Transient input list failure.");
                }

                return items[index];
            }
            set => items[index] = value;
        }

        public int Count => items.Count;
        public bool IsReadOnly => false;
        public void Add(IBatchContainer item) => items.Add(item);
        public void Clear() => items.Clear();
        public bool Contains(IBatchContainer item) => items.Contains(item);
        public void CopyTo(IBatchContainer[] array, int arrayIndex) => items.CopyTo(array, arrayIndex);
        public IEnumerator<IBatchContainer> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int IndexOf(IBatchContainer item) => items.IndexOf(item);
        public void Insert(int index, IBatchContainer item) => items.Insert(index, item);
        public bool Remove(IBatchContainer item) => items.Remove(item);
        public void RemoveAt(int index) => items.RemoveAt(index);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public bool FailNextLog { get; set; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (FailNextLog)
            {
                FailNextLog = false;
                throw new InvalidOperationException("Transient logging failure.");
            }
        }
    }
}
