using System.Text;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class JournalCatalogRangeTests
{
    [Theory]
    [InlineData(null, null, null, "anything", true)]
    [InlineData("jobs/shards/20260909", null, null, "jobs/shards/20260909T120000-a", true)]
    [InlineData("jobs/shards/20260909", null, null, "jobs/shards/20260910T120000-a", false)]
    [InlineData("tenant", null, null, "tenant2", true)]
    [InlineData("tenant/", null, null, "tenant2", false)]
    [InlineData(null, "b", "d", "b", true)]
    [InlineData(null, "b", "d", "d", true)]
    [InlineData(null, "b", "d", "a", false)]
    [InlineData(null, "b", "d", "e", false)]
    [InlineData("a", "b", "z", "abc", false)]
    [InlineData(null, "z", "a", "m", false)]
    public void Contains_IntersectsRawPrefixAndInclusiveBounds(string? prefix, string? min, string? max, string id, bool expected)
    {
        var range = new JournalCatalogRange(new()
        {
            Prefix = ToId(prefix),
            MinId = ToId(min),
            MaxId = ToId(max)
        });

        Assert.Equal(expected, range.Contains(id));
    }

    [Fact]
    public void Snapshot_RetainsBoundsAndDerivesNarrowerNativePrefix()
    {
        var options = new ListOptions
        {
            Prefix = new("jobs/"),
            MinId = new("jobs/shards/20260909-a"),
            MaxId = new("jobs/shards/20260909-z")
        };
        var range = new JournalCatalogRange(options);
        options.Prefix = new("other/");
        options.MinId = new("z");
        options.MaxId = new("z");

        Assert.Equal("jobs/shards/20260909-", range.ListingPrefix);
        Assert.Equal("jobs/shards/20260909-a", range.LowerBound);
        Assert.Equal("jobs/shards/20260909-z", range.UpperBound);
        Assert.True(range.Contains("jobs/shards/20260909-m"));
        Assert.False(range.Contains("jobs/shards/20260908-m"));
    }

    [Theory]
    [InlineData("a", "b", null)]
    [InlineData("b", null, "a")]
    [InlineData(null, "z", "a")]
    [InlineData("other/", "jobs/a", "jobs/z")]
    public void DisjointConstraints_AreEmpty(string? prefix, string? min, string? max)
    {
        var range = new JournalCatalogRange(new() { Prefix = ToId(prefix), MinId = ToId(min), MaxId = ToId(max) });

        Assert.True(range.IsEmpty);
    }

    [Fact]
    public void NativePrefix_PreservesMatchingIdsWithoutSplittingSurrogatePairs()
    {
        var range = new JournalCatalogRange(new()
        {
            MinId = new("tenant/\U0001F600-a"),
            MaxId = new("tenant/\U0001F601-z")
        });

        Assert.Equal("tenant/", range.ListingPrefix);
        Assert.True(range.Contains("tenant/\U0001F600-b"));
        Assert.True(range.Contains("tenant/\U0001F601-a"));
        Assert.Null(range.GetUpperBoundForSuffix("/wal"));
    }

    [Theory]
    [InlineData(0xD800, "", "suffix")]
    [InlineData(0xDBFF, "", "")]
    [InlineData(0xDC00, "", "suffix")]
    [InlineData(0xDFFF, "", "")]
    [InlineData(0xD800, "tenant/", "/suffix")]
    [InlineData(0xDBFF, "tenant/", "")]
    [InlineData(0xDC00, "tenant/", "/suffix")]
    [InlineData(0xDFFF, "tenant/", "")]
    [InlineData(0xD800, "tenant/\U0001F600/", "/\U0001F601")]
    [InlineData(0xDC00, "tenant/\U0001F600/", "/\U0001F601")]
    public void NativePrefix_BroadensBeforeFirstUnpairedSurrogateAndKeepsOrdinalBounds(
        int surrogate, string before, string after)
    {
        // Construct malformed UTF-16 at runtime so attribute serialization preserves the intended code unit.
        var prefix = before + (char)surrogate + after;
        var min = new JournalId(prefix + "b");
        var max = new JournalId(prefix + "d");
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var fromBounds in new[] { false, true })
        {
            var options = new ListOptions
            {
                Prefix = fromBounds ? default : new(prefix),
                MinId = min,
                MaxId = max
            };
            var range = new JournalCatalogRange(options);
            options.Prefix = new("changed");
            options.MinId = default;
            options.MaxId = default;

            Assert.Equal(before.Length == 0 ? null : before, range.ListingPrefix);
            Assert.Equal(fromBounds ? null : prefix, range.Prefix);
            Assert.Equal(min.Value, range.MinId);
            Assert.Equal(max.Value, range.MaxId);
            Assert.Equal(before, strictUtf8.GetString(strictUtf8.GetBytes(range.ListingPrefix ?? string.Empty)));
            Assert.True(range.Contains(min.Value));
            Assert.True(range.Contains(prefix + "c"));
            Assert.True(range.Contains(max.Value));
            Assert.False(range.Contains(prefix + "a"));
            Assert.False(range.Contains(prefix + "e"));
            Assert.False(range.Contains(before + "other"));
        }
    }

    [Theory]
    [InlineData("tenant/ascii")]
    [InlineData("tenant/\u00e9")]
    [InlineData("tenant/\U0001F600")]
    [InlineData("\U00010000/\U0010FFFF")]
    public void NativePrefix_PreservesCompleteCharacters(string prefix)
    {
        var range = new JournalCatalogRange(new() { Prefix = new(prefix) });

        Assert.Equal(prefix, range.ListingPrefix);
        Assert.True(range.Contains(prefix + "/child"));
        Assert.False(range.Contains("other"));
    }

    [Fact]
    public void StorageUpperBound_IncludesEveryMatchingSuffixedKey()
    {
        string[] ids = ["a", "a!", "a.", "a/", "a/child", "a/wal", "a0", "aa", "b"];
        string?[] prefixes = [null, "a", "a/"];
        string?[] minimums = [null, "a", "a/child"];
        foreach (var prefix in prefixes)
        {
            foreach (var minimum in minimums)
            {
                foreach (var maximum in ids)
                {
                    var range = new JournalCatalogRange(new()
                    {
                        Prefix = ToId(prefix),
                        MinId = ToId(minimum),
                        MaxId = new(maximum)
                    });
                    var upper = range.GetUpperBoundForSuffix("/wal");
                    Assert.NotNull(upper);
                    foreach (var id in ids.Where(range.Contains))
                    {
                        Assert.True(string.CompareOrdinal(id + "/wal", upper) <= 0,
                            $"WAL for {id} exceeds cutoff {upper}; prefix={prefix}, min={minimum}, max={maximum}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void DescendantRange_DoesNotExpandCutoffToNamespaceWal()
    {
        const string maximum = "jobs/shards/20260909T1200000000000Z~";
        var range = new JournalCatalogRange(new()
        {
            Prefix = new("jobs/shards/"),
            MaxId = new(maximum)
        });

        Assert.Equal(maximum + "/wal", range.GetUpperBoundForSuffix("/wal"));
        Assert.False(range.Contains("jobs/shards"));
    }

    private static JournalId ToId(string? value) => value is null ? default : new(value);
}
