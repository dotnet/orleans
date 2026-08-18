using Orleans.Runtime;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class StripedCallbackDictionaryTests
{
    [Fact]
    public void CorrelationIdsRetainStripeIndex()
    {
        for (var stripe = 0; stripe < StripedCallbackDictionary<int>.StripeCount; stripe++)
        {
            var id = StripedCallbackDictionary<int>.CreateCorrelationId(42, stripe);
            Assert.Equal(stripe, StripedCallbackDictionary<int>.GetStripeIndex(id));
        }
    }

    [Fact]
    public void AddGetAndRemovePreserveValue()
    {
        var dictionary = new StripedCallbackDictionary<string>();
        var id = StripedCallbackDictionary<string>.CreateCorrelationId(42, 7);

        Assert.True(dictionary.TryAdd(id, "value"));
        Assert.False(dictionary.TryAdd(id, "duplicate"));
        Assert.True(dictionary.TryGetValue(id, out var value));
        Assert.Equal("value", value);
        Assert.True(dictionary.TryRemove(id, out value));
        Assert.Equal("value", value);
        Assert.False(dictionary.TryGetValue(id, out _));
    }

    [Fact]
    public void EnumerationReturnsSnapshotValues()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        for (var i = 0; i < 32; i++)
        {
            var id = StripedCallbackDictionary<int>.CreateCorrelationId(i, i);
            Assert.True(dictionary.TryAdd(id, i));
        }

        Assert.Equal(Enumerable.Range(0, 32), dictionary.Select(pair => pair.Value).Order());
    }

    [Fact]
    public void ConcurrentOperationsPreserveAllEntries()
    {
        var dictionary = new StripedCallbackDictionary<int>();

        Parallel.For(0, 10_000, i =>
        {
            var id = StripedCallbackDictionary<int>.CreateCorrelationId(i, i);
            Assert.True(dictionary.TryAdd(id, i));
        });

        Assert.Equal(10_000, dictionary.Count);
        Assert.Equal(10_000, dictionary.CountWhere(static pair => pair.Value >= 0));
    }
}
