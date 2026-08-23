using Orleans.Runtime;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class StripedCallbackDictionaryTests
{
    private static readonly GrainId Owner = GrainId.Create("test", "owner");

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

        Assert.True(dictionary.TryAdd(Owner, id, "value"));
        Assert.False(dictionary.TryAdd(Owner, id, "duplicate"));
        Assert.True(dictionary.TryGetValue(Owner, id, out var value));
        Assert.Equal("value", value);
        Assert.True(dictionary.TryRemove(Owner, id, out value));
        Assert.Equal("value", value);
        Assert.False(dictionary.TryGetValue(Owner, id, out _));
    }

    [Fact]
    public void CallbackOwnerIsPartOfTheKey()
    {
        var dictionary = new StripedCallbackDictionary<string>();
        var otherOwner = GrainId.Create("test", "other-owner");
        var id = new CorrelationId(42);

        Assert.True(dictionary.TryAdd(Owner, id, "value"));
        Assert.False(dictionary.TryGetValue(otherOwner, id, out _));
        Assert.False(dictionary.TryRemove(otherOwner, id, out _));
        Assert.True(dictionary.TryGetValue(Owner, id, out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void EnumerationReturnsSnapshotValues()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        for (var i = 0; i < 32; i++)
        {
            var id = new CorrelationId(i);
            Assert.True(dictionary.TryAdd(Owner, id, i));
        }

        Assert.Equal(Enumerable.Range(0, 32), dictionary.Order());
    }

    [Fact]
    public void ConcurrentOperationsPreserveCountAndValues()
    {
        var dictionary = new StripedCallbackDictionary<int>();

        Parallel.For(0, 10_000, i =>
        {
            var id = new CorrelationId(i);
            Assert.True(dictionary.TryAdd(Owner, id, i));
            Assert.True(dictionary.TryGetValue(Owner, id, out var value));
            Assert.Equal(i, value);
        });

        Assert.Equal(10_000, dictionary.Count);
        Assert.Equal(10_000, dictionary.CountWhere(static value => value >= 0));

        Parallel.For(0, 10_000, i =>
        {
            Assert.True(dictionary.TryRemove(Owner, new CorrelationId(i), out var value));
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
            Assert.True(dictionary.TryAdd(Owner, new CorrelationId(i), i));
        }

        Parallel.Invoke(
            () => Parallel.For(0, count, i =>
            {
                if (dictionary.TryGetValue(Owner, new CorrelationId(i), out var value))
                {
                    Assert.Equal(i, value);
                }
            }),
            () => Parallel.For(0, count, i =>
            {
                Assert.True(dictionary.TryRemove(Owner, new CorrelationId(i), out var value));
                Assert.Equal(i, value);
            }));

        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void DisposingPartialEnumerationLeavesDictionaryUsable()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        for (var i = 0; i < 32; i++)
        {
            Assert.True(dictionary.TryAdd(Owner, new CorrelationId(i), i));
        }

        using (var enumerator = dictionary.GetEnumerator())
        {
            Assert.True(enumerator.MoveNext());
        }

        Assert.True(dictionary.TryRemove(Owner, new CorrelationId(0), out var value));
        Assert.Equal(0, value);
    }
}
