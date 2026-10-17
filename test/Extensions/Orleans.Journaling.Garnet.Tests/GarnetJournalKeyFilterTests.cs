using System.Text;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests the key filters partitions write when they seal a bucket.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalKeyFilterTests
{
    [Fact]
    public void Filter_ReportsEveryId_AndFewOthers()
    {
        var held = Enumerable.Range(0, 5000).Select(i => Encoding.UTF8.GetBytes($"held/{i}")).ToList();
        var filter = GarnetJournalKeyFilter.Create(held);
        Assert.All(held, id => Assert.False(GarnetJournalKeyFilter.ExcludesAll(filter, id)));

        var falsePositives = Enumerable.Range(0, 100_000).Count(i => !GarnetJournalKeyFilter.ExcludesAll(filter, Encoding.UTF8.GetBytes($"other/{i}")));
        Assert.InRange(falsePositives / 100_000.0, 0, 0.02);
        Assert.InRange(filter.Length, 6000, 7000);
    }

    [Fact]
    public void Filters_ExcludeAnIdOnlyIfNoFilterReportsIt()
    {
        var first = GarnetJournalKeyFilter.Create([Encoding.UTF8.GetBytes("a")]);
        var second = GarnetJournalKeyFilter.Create([Encoding.UTF8.GetBytes("b")]);
        var chain = first.Concat(second).ToArray();
        Assert.Equal(2, GarnetJournalKeyFilter.Count(chain));
        Assert.False(GarnetJournalKeyFilter.ExcludesAll(chain, Encoding.UTF8.GetBytes("a")));
        Assert.False(GarnetJournalKeyFilter.ExcludesAll(chain, Encoding.UTF8.GetBytes("b")));
        Assert.True(GarnetJournalKeyFilter.ExcludesAll(chain, Encoding.UTF8.GetBytes("c")));

        // An empty filter excludes everything; no filters, or malformed ones, exclude nothing.
        Assert.True(GarnetJournalKeyFilter.ExcludesAll(GarnetJournalKeyFilter.Create([]), Encoding.UTF8.GetBytes("a")));
        Assert.False(GarnetJournalKeyFilter.ExcludesAll([], Encoding.UTF8.GetBytes("c")));
        Assert.False(GarnetJournalKeyFilter.ExcludesAll(chain.AsSpan(0, chain.Length - 1), Encoding.UTF8.GetBytes("c")));
    }
}
