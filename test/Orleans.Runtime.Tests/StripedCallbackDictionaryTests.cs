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
    public void GetStripeIndex_OverflowAndStride_DistributesCorrelationIds()
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
    public void AddGetAndRemove_ValueIdentityIsPreserved()
    {
        var dictionary = new StripedCallbackDictionary<object>();
        var id = new CorrelationId(42);
        var value = new object();

        Assert.True(dictionary.TryAdd(id, value));
        Assert.False(dictionary.TryAdd(id, new object()));
        Assert.True(dictionary.TryGetValue(id, out var found));
        Assert.Same(value, found);
        Assert.False(dictionary.TryRemove(id, new object()));
        Assert.True(dictionary.TryRemove(id, value));
        Assert.False(dictionary.TryGetValue(id, out _));
    }

    [Fact]
    public void ConcurrentOperations_CountAndValuesRemainExact()
    {
        var dictionary = new StripedCallbackDictionary<int>();

        Parallel.For(0, 10_000, index =>
        {
            var id = new CorrelationId(index);
            Assert.True(dictionary.TryAdd(id, index));
            Assert.True(dictionary.TryGetValue(id, out var value));
            Assert.Equal(index, value);
        });

        Assert.Equal(10_000, dictionary.Count);
        Assert.Equal(10_000, dictionary.CountWhere(0, static (value, _) => value >= 0));

        Parallel.For(0, 10_000, index =>
        {
            Assert.True(dictionary.TryRemove(new CorrelationId(index), out var value));
            Assert.Equal(index, value);
        });

        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void ForEach_SnapshotAllowsValuesToRemoveThemselves()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        for (var index = 0; index < 32; index++)
        {
            Assert.True(dictionary.TryAdd(new CorrelationId(index), index));
        }

        dictionary.ForEach(dictionary, static (value, dictionary) =>
        {
            Assert.True(dictionary.TryRemove(new CorrelationId(value), out var removed));
            Assert.Equal(value, removed);
        });

        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void ForEach_EmptyDictionary_DoesNotAllocate()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        dictionary.ForEach((object?)null, EmptyVisitor);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        dictionary.ForEach((object?)null, EmptyVisitor);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void CountWhere_StatefulPredicate_DoesNotAllocate()
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

    [Fact]
    public void Close_ClosesAdmissionAndRetainsPublishedValues()
    {
        var dictionary = new StripedCallbackDictionary<int>();
        Assert.True(dictionary.TryAdd(new CorrelationId(1), 1));

        dictionary.Close();

        Assert.False(dictionary.TryAdd(new CorrelationId(2), 2, out var isClosed));
        Assert.True(isClosed);
        Assert.Equal(1, dictionary.Count);
        Assert.True(dictionary.TryRemove(new CorrelationId(1), out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public async Task Close_ConcurrentPublication_ClassifiesEveryAttempt()
    {
        const int publisherCount = 32;
        const int entriesPerPublisher = 1_024;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var start = new Barrier(publisherCount + 1);
        var dictionary = new StripedCallbackDictionary<int>();
        var accepted = new bool[publisherCount * entriesPerPublisher];
        var tasks = Enumerable.Range(0, publisherCount)
            .Select(publisher => Task.Run(() =>
            {
                start.SignalAndWait(cancellationToken);
                var offset = publisher * entriesPerPublisher;
                for (var index = 0; index < entriesPerPublisher; index++)
                {
                    var value = offset + index;
                    accepted[value] = dictionary.TryAdd(new CorrelationId(value), value, out var isClosed);
                    Assert.Equal(!accepted[value], isClosed);
                }
            }, cancellationToken))
            .ToArray();

        start.SignalAndWait(cancellationToken);
        dictionary.Close();
        await Task.WhenAll(tasks);

        Assert.Equal(accepted.Count(static value => value), dictionary.Count);
        for (var value = 0; value < accepted.Length; value++)
        {
            Assert.Equal(accepted[value], dictionary.TryGetValue(new CorrelationId(value), out var found));
            if (accepted[value])
            {
                Assert.Equal(value, found);
            }
        }

        Assert.False(dictionary.TryAdd(new CorrelationId(accepted.Length), accepted.Length, out var isClosed));
        Assert.True(isClosed);
    }
}
