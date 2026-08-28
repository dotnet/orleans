using Orleans.Runtime;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class StripedCallbackDictionaryTests
{
    private static readonly Action<int, object?> EmptyVisitor = static (_, _) => { };
    private static readonly Func<int, int, bool> MatchValue = static (value, expected) => value == expected;

    [Fact]
    public void CorrelationIdsDistributeAcrossStripesAtOverflowAndWithStride()
    {
        var start = long.MaxValue - (StripedCallbackDictionary<int>.StripeCount / 2);
        var consecutiveStripes = Enumerable.Range(0, StripedCallbackDictionary<int>.StripeCount)
            .Select(offset => new CorrelationId(unchecked(start + offset)))
            .Select(StripedCallbackDictionary<int>.GetStripeIndex)
            .Distinct()
            .Count();
        var stridedStripes = Enumerable.Range(0, StripedCallbackDictionary<int>.StripeCount)
            .Select(offset => new CorrelationId(offset * StripedCallbackDictionary<int>.StripeCount))
            .Select(StripedCallbackDictionary<int>.GetStripeIndex)
            .Distinct()
            .Count();

        Assert.True(consecutiveStripes > StripedCallbackDictionary<int>.StripeCount / 2);
        Assert.True(stridedStripes > StripedCallbackDictionary<int>.StripeCount / 2);
    }

    [Fact]
    public void AddGetAndRemovePreserveValue()
    {
        var dictionary = new StripedCallbackDictionary<string>();
        var id = new CorrelationId(42);

        Assert.True(dictionary.TryAdd(id, "value"));
        Assert.False(dictionary.TryAdd(id, "duplicate"));
        Assert.True(dictionary.TryGetValue(id, out var value));
        Assert.Equal("value", value);
        Assert.True(dictionary.TryRemove(id, out value));
        Assert.Equal("value", value);
        Assert.False(dictionary.TryGetValue(id, out _));
    }

    [Fact]
    public void ExactRemovalDoesNotRemoveReplacement()
    {
        var dictionary = new StripedCallbackDictionary<object>();
        var id = new CorrelationId(42);
        var stale = new object();
        var replacement = new object();

        Assert.True(dictionary.TryAdd(id, stale));
        Assert.True(dictionary.TryRemove(id, out var removed));
        Assert.Same(stale, removed);
        Assert.True(dictionary.TryAdd(id, replacement));

        Assert.False(dictionary.TryRemove(id, stale));
        Assert.True(dictionary.TryGetValue(id, out var current));
        Assert.Same(replacement, current);
        Assert.True(dictionary.TryRemove(id, replacement));
        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void ExactRemovalSupportsValueTypes()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        var id = new CorrelationId(42);

        Assert.True(dictionary.TryAdd(id, 1));
        Assert.False(dictionary.TryRemove(id, 2));
        Assert.True(dictionary.TryGetValue(id, out var current));
        Assert.Equal(1, current);
        Assert.True(dictionary.TryRemove(id, 1));
        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void EnumerationReturnsSnapshotValues()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        for (var i = 0; i < 32; i++)
        {
            var id = new CorrelationId(i);
            Assert.True(dictionary.TryAdd(id, i));
        }

        var values = new List<int>();
        dictionary.ForEach(values, static (value, values) => values.Add(value));

        Assert.Equal(Enumerable.Range(0, 32), values.Order());
    }

    [Fact]
    public void ConcurrentOperationsPreserveCountAndValues()
    {
        var dictionary = new StripedCallbackDictionary<int>();

        Parallel.For(0, 10_000, i =>
        {
            var id = new CorrelationId(i);
            Assert.True(dictionary.TryAdd(id, i));
            Assert.True(dictionary.TryGetValue(id, out var value));
            Assert.Equal(i, value);
        });

        Assert.Equal(10_000, dictionary.Count);
        Assert.Equal(10_000, dictionary.CountWhere(static value => value >= 0));

        Parallel.For(0, 10_000, i =>
        {
            Assert.True(dictionary.TryRemove(new CorrelationId(i), out var value));
            Assert.Equal(i, value);
        });

        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void ConcurrentLookupAndRemovalRemainConsistent()
    {
        const int count = 10_000;
        var dictionary = new StripedCallbackDictionary<int>();
        for (var i = 0; i < count; i++)
        {
            Assert.True(dictionary.TryAdd(new CorrelationId(i), i));
        }

        Parallel.Invoke(
            () => Parallel.For(0, count, i =>
            {
                if (dictionary.TryGetValue(new CorrelationId(i), out var value))
                {
                    Assert.Equal(i, value);
                }
            }),
            () => Parallel.For(0, count, i =>
            {
                Assert.True(dictionary.TryRemove(new CorrelationId(i), out var value));
                Assert.Equal(i, value);
            }));

        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void SnapshotVisitorAllowsValuesToRemoveThemselves()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        for (var i = 0; i < 32; i++)
        {
            Assert.True(dictionary.TryAdd(new CorrelationId(i), i));
        }

        dictionary.ForEach(dictionary, static (value, dictionary) =>
        {
            Assert.True(dictionary.TryRemove(new CorrelationId(value), out var removed));
            Assert.Equal(value, removed);
        });

        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void EmptyVisitorDoesNotAllocate()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        dictionary.ForEach((object?)null, EmptyVisitor);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        dictionary.ForEach((object?)null, EmptyVisitor);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void StatefulCountDoesNotAllocate()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        Assert.True(dictionary.TryAdd(new CorrelationId(42), 42));
        Assert.Equal(1, dictionary.CountWhere(42, MatchValue));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var count = dictionary.CountWhere(42, MatchValue);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(1, count);
        Assert.Equal(0, allocated);
    }
}
