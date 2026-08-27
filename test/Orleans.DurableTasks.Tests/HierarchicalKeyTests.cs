using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using HierarchicalKey = System.Distributed.DurableTasks.HierarchicalKey;
using OrleansHierarchicalKey = Orleans.HierarchicalKey;
using TaskId = System.Distributed.DurableTasks.TaskId;

namespace Orleans.DurableTasks.Tests;

[Trait("Category", "BVT")]
public class HierarchicalKeyTests
{
    [Fact]
    public void RepresentationIsInconsequential()
    {
        var aParent = HierarchicalKey.Create("foo/bar");
        var a = aParent.CreateChildKey("baz");
        var b = HierarchicalKey.Create("foo/bar/baz");
        Assert.Equal(a, b);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.ToString().Length, a.Length);
        Assert.Equal(b.ToString().Length, b.Length);
        Assert.Equal(a.Length, b.Length);

        var aSegments = new List<string>();
        foreach (var segment in a)
        {
            aSegments.Add(segment.ToString());
        }

        var bSegments = new List<string>();
        foreach (var segment in b)
        {
            bSegments.Add(segment.ToString());
        }

        Assert.Equal(aSegments.Count, bSegments.Count);
        Assert.Equal(aSegments, bSegments);
    }

    [Fact]
    public void SegmentsCanBeEscaped()
    {
        var aParent = HierarchicalKey.Create("foo/bar\\/");
        var a = aParent.CreateChildKey("baz");
        var b = HierarchicalKey.Create("foo/bar\\//baz");
        Assert.Equal(a, b);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.ToString().Length, a.Length);
        Assert.Equal(b.ToString().Length, b.Length);
        Assert.Equal(a.Length, b.Length);

        var aSegments = new List<string>();
        foreach (var segment in a)
        {
            aSegments.Add(segment.ToString());
        }

        var bSegments = new List<string>();
        foreach (var segment in b)
        {
            bSegments.Add(segment.ToString());
        }

        Assert.Equal(aSegments.Count, bSegments.Count);

        Assert.Equal(aSegments, bSegments);
    }

    [Fact]
    public void OrleansEscapedKeyCopiesMutableInput()
    {
        var value = "value".ToCharArray();
        var key = OrleansHierarchicalKey.CreateEscaped(parent: null, value);

        value[0] = 'X';

        Assert.Equal("value", key.ToString());
    }

    [Fact]
    public void DeepKeysFormatIteratively()
    {
        OrleansHierarchicalKey orleansKey = OrleansHierarchicalKey.Create("root");
        HierarchicalKey durableTaskKey = HierarchicalKey.Create("root");
        for (var i = 0; i < 10_000; i++)
        {
            orleansKey = orleansKey.CreateChildKey("x");
            durableTaskKey = durableTaskKey.CreateChildKey("x");
        }

        Assert.Equal(orleansKey.Length, orleansKey.ToString().Length);
        Assert.Equal(durableTaskKey.Length, durableTaskKey.ToString().Length);
    }

    [Fact]
    public void OnlyValidValuesAreAllowed()
    {
        Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Create(null!));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(""));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("/"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("//"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a//"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("//a"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("\\//"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a/b//c/d"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("aaa/bbb//ccc/ddd"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a/b/c/d//"));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("//a/b/c/d//"));
        _ = HierarchicalKey.Create("\\/\\/");
        _ = HierarchicalKey.Create("aaa/bbb/ccc/ddd");
        _ = HierarchicalKey.Create("a/b/c/d");
        _ = HierarchicalKey.Create("\\/\\/a/b/c/d\\/\\/");
    }

    [Fact]
    public void GetParentTest()
    {
        var aKey = HierarchicalKey.Create("aaa");

        Assert.True(aKey.IsParentOf(HierarchicalKey.Create(aKey, "bbb")));
        Assert.True(aKey.IsAncestorOf(HierarchicalKey.Create("aaa/bbb/ccc")));
        Assert.True(aKey.IsParentOf(HierarchicalKey.Create("aaa/bbb")));
        Assert.False(aKey.IsParentOf(HierarchicalKey.Create("aaa/bbb/ccc")));
        Assert.False(aKey.IsAncestorOf(HierarchicalKey.Create("bbb/ccc")));
        Assert.False(HierarchicalKey.Create("a").IsAncestorOf(HierarchicalKey.Create("aa")));

        Assert.True(aKey.IsAncestorOf(aKey));
        Assert.False(aKey.IsParentOf(aKey));
        Assert.False(aKey.IsParentOf(HierarchicalKey.Create("aaa")));
        Assert.False(aKey.IsParentOf(HierarchicalKey.Create("bbb")));

        Assert.Null(aKey.GetParent());
        Assert.Same(aKey, HierarchicalKey.Create(aKey, "bbb").GetParent());
        Assert.True(HierarchicalKey.Create("aaa/bbb").IsChildOf(aKey));
        Assert.False(HierarchicalKey.Create("aaa/bbb/ccc").IsChildOf(aKey));
        Assert.Equal(HierarchicalKey.Create(aKey, "bbb"), HierarchicalKey.Create("aaa/bbb/ccc").GetParent());
        Assert.True(HierarchicalKey.Create("aaa/bbb").IsParentOf(HierarchicalKey.Create("aaa/bbb/ccc")));
        Assert.Equal(HierarchicalKey.Create("aaa/bbb"), HierarchicalKey.Create("aaa/bbb/ccc").GetParent());

        Assert.Null(HierarchicalKey.Create("\\/\\/").GetParent());
        Assert.Null(HierarchicalKey.Create("\\/").GetParent());
        Assert.Equal(HierarchicalKey.Create("\\/\\/"), HierarchicalKey.Create("\\/\\//aaa").GetParent());
        Assert.Equal(HierarchicalKey.Create("\\/"), HierarchicalKey.Create("\\//\\/").GetParent());
    }

    [Fact]
    public void CreateEscapedChildKeyTest()
    {
        var aParent = HierarchicalKey.Create("foo/bar\\/");
        var a = aParent.CreateEscapedChildKey("baz/boz");
        var b = aParent.CreateEscapedChildKey("baz\\/boz");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a.ToString(), b.ToString());
        Assert.Equal(a, HierarchicalKey.Parse(a.ToString(), provider: null));
        Assert.Equal(b, HierarchicalKey.Parse(b.ToString(), provider: null));
        Assert.Equal(a.ToString().Length, a.Length);
        Assert.Equal(b.ToString().Length, b.Length);

        var aSegments = new List<string>();
        foreach (var segment in a)
        {
            aSegments.Add(segment.ToString());
        }

        var bSegments = new List<string>();
        foreach (var segment in b)
        {
            bSegments.Add(segment.ToString());
        }

        Assert.Equal(aSegments.Count, bSegments.Count);
        Assert.Equal(aSegments.Take(aSegments.Count - 1), bSegments.Take(bSegments.Count - 1));
        Assert.NotEqual(aSegments[^1], bSegments[^1]);
    }

    [Fact]
    public void IsAncestorOf_WithNullOther_ReturnsFalse()
    {
        var key = HierarchicalKey.Create("aaa");
        Assert.False(key.IsAncestorOf(null));
    }

    [Fact]
    public void IsAncestorOf_WhenLeftHasMoreSegmentsThanRight_ReturnsFalse()
    {
        // Exercises the "leftValid && !rightValid" branch: the candidate ancestor is longer
        // than the candidate descendant, sharing a common prefix, but cannot be an ancestor.
        var longer = HierarchicalKey.Create("aaa/bbb");
        var shorter = HierarchicalKey.Create("aaa");

        Assert.False(longer.IsAncestorOf(shorter));

        // Contrast: the same pair in the other direction (right longer) is a valid ancestor relationship.
        Assert.True(shorter.IsAncestorOf(longer));
    }

    [Fact]
    public void IsAncestorOf_WhenSegmentsDivergeAfterCommonPrefix_ReturnsFalse()
    {
        var a = HierarchicalKey.Create("aaa/bbb/ccc");
        var b = HierarchicalKey.Create("aaa/bbb/ddd");

        Assert.False(a.IsAncestorOf(b));
        Assert.False(b.IsAncestorOf(a));
    }

    [Theory]
    [InlineData("a/b", true)]
    [InlineData("aaa/bbb/ccc", true)]
    [InlineData("\\/\\/", true)]
    [InlineData("\\/\\//aaa", true)]
    public void TryParse_String_WithValidValue_ReturnsTrueAndEquivalentKey(string value, bool expectedSuccess)
    {
        var success = HierarchicalKey.TryParse(value, provider: null, out var result);

        Assert.Equal(expectedSuccess, success);
        Assert.NotNull(result);
        Assert.Equal(HierarchicalKey.Create(value), result);
        Assert.Equal(value, result!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("//")]
    [InlineData("a/")]
    [InlineData("\\//")]
    public void TryParse_String_WithNullEmptyOrInvalidSegmentation_ReturnsFalseAndNullResult(string? value)
    {
        var success = HierarchicalKey.TryParse(value, provider: null, out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Span_WithValidValue_ReturnsTrueAndEquivalentKey()
    {
        var success = HierarchicalKey.TryParse("aaa/bbb/ccc".AsSpan(), provider: null, out var result);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(HierarchicalKey.Create("aaa/bbb/ccc"), result);
        Assert.Equal("aaa/bbb/ccc", result!.ToString());
    }

    [Fact]
    public void TryParse_Span_WithEmptySpan_ReturnsFalseAndNullResult()
    {
        var success = HierarchicalKey.TryParse(ReadOnlySpan<char>.Empty, provider: null, out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Span_WithInvalidSegmentation_ReturnsFalseAndNullResult()
    {
        var success = HierarchicalKey.TryParse("a//b".AsSpan(), provider: null, out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    // GetLastSegment is a private static helper. It is not currently invoked anywhere else in this
    // type (its sibling WithoutLastSegment is used by GetParent instead), so it is only reachable via
    // reflection. It has real branching logic around escaped separators, so it is exercised directly
    // rather than left completely untested.
    private delegate ReadOnlySpan<char> GetLastSegmentDelegate(ReadOnlySpan<char> value);

    private static readonly GetLastSegmentDelegate GetLastSegment = (GetLastSegmentDelegate)Delegate.CreateDelegate(
        typeof(GetLastSegmentDelegate),
        typeof(HierarchicalKey).GetMethod("GetLastSegment", BindingFlags.NonPublic | BindingFlags.Static)!);

    [Theory]
    [InlineData("aaa/bbb/ccc", "ccc")]
    [InlineData("aaa", "aaa")]
    [InlineData("aaa/bbb\\/ccc", "bbb\\/ccc")]
    [InlineData("", "")]
    public void GetLastSegment_ReturnsFinalUnescapedSeparatedSegment(string value, string expectedLastSegment)
    {
        var result = GetLastSegment(value.AsSpan());

        Assert.Equal(expectedLastSegment, result.ToString());
    }

    [Fact]
    public void GetLastSegment_WithTrailingEscapedSeparatorInLastSegment_TreatsItAsPartOfTheSegment()
    {
        // The final separator is escaped, so the whole escaped value is one segment: no unescaped
        // separator exists, so lastSegmentStart stays at 0 and the entire span is returned.
        var result = GetLastSegment("aaa\\/".AsSpan());

        Assert.Equal("aaa\\/", result.ToString());
    }

    [Fact]
    public void EveryChildFactoryRejectsNullEmptyAndMalformedChildren()
    {
        var parent = HierarchicalKey.Create("parent");

        Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Create(parent, null!));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(parent, ""));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(parent, "child\\"));
        Assert.Throws<ArgumentNullException>(() => parent.CreateChildKey(null!));
        Assert.Throws<ArgumentException>(() => parent.CreateChildKey(""));
        Assert.Throws<ArgumentException>(() => parent.CreateChildKey("child\\q"));
        Assert.Throws<ArgumentNullException>(() => parent.CreateEscapedChildKey(null!));
        Assert.Throws<ArgumentException>(() => parent.CreateEscapedChildKey(""));
        Assert.Throws<ArgumentNullException>(() => HierarchicalKey.CreateEscaped(null!));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.CreateEscaped(""));
    }

    [Fact]
    public void EscapedChildPreservesEqualityFormattingAndHashAcrossRepresentations()
    {
        var parent = HierarchicalKey.Create("root");
        var composed = parent.CreateEscapedChildKey("slash/and\\escape");
        var parsed = HierarchicalKey.Parse(composed.ToString(), provider: null);

        Assert.Equal(composed, parsed);
        Assert.Equal(composed.ToString(), parsed.ToString());
        Assert.Equal(composed.GetHashCode(), parsed.GetHashCode());
        Assert.True(composed.IsChildOf(parent));
    }

    [Fact]
    public void PublicHierarchicalKeyChildFactoriesRejectInvalidValuesAndPreserveValueSemantics()
    {
        var parent = OrleansHierarchicalKey.Create("root");
        Assert.Throws<ArgumentNullException>(() => OrleansHierarchicalKey.Create(parent, null!));
        Assert.Throws<ArgumentException>(() => OrleansHierarchicalKey.Create(parent, ""));
        Assert.Throws<ArgumentException>(() => parent.CreateChildKey("child\\q"));
        Assert.Throws<ArgumentNullException>(() => parent.CreateEscapedChildKey(null!));
        Assert.Throws<ArgumentException>(() => parent.CreateEscapedChildKey(""));

        var composed = parent.CreateEscapedChildKey("slash/and\\escape");
        var parsed = OrleansHierarchicalKey.Parse(composed.ToString(), provider: null);
        Assert.Equal(composed, parsed);
        Assert.Equal(composed.ToString(), parsed.ToString());
        Assert.Equal(composed.GetHashCode(), parsed.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\\")]
    [InlineData("a\\q")]
    [InlineData("a//b")]
    public void TaskIdRejectsEmptyAndMalformedEscapedValues(string value)
    {
        Assert.ThrowsAny<Exception>(() => TaskId.Parse(value));
        Assert.False(TaskId.TryParse(value, provider: null, out _));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("separator/value")]
    [InlineData("backslash\\value")]
    public void TaskIdCreateStringAndChildRoundTrips(string value)
    {
        var created = TaskId.Create(value);
        Assert.Equal(created, TaskId.Parse(created.ToString()));
        Assert.Equal(created, (TaskId)(string)created);

        var child = TaskId.Create("parent").Child(value);
        Assert.Equal(child, TaskId.Parse(child.ToString()));
        Assert.True(child.IsChildOf(TaskId.Create("parent")));
    }
}
