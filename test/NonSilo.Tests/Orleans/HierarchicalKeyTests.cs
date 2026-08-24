using Xunit;

namespace NonSilo.Tests.Orleans;

/// <summary>
/// Tests for HierarchicalKey, which provides a way to represent hierarchical identifiers
/// using slash-separated segments similar to file paths or URLs.
/// </summary>
[TestCategory("BVT")]
public class HierarchicalKeyTests
{
    [Fact]
    public void Create_WithValidString_CreatesKey()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo");
        Assert.NotNull(key);
        Assert.Equal("foo", key.ToString());
    }

    [Fact]
    public void Create_WithMultipleSegments_CreatesKey()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo/bar/baz");
        Assert.NotNull(key);
        Assert.Equal("foo/bar/baz", key.ToString());
    }

    [Fact]
    public void Create_WithEmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create(""));
    }

    [Fact]
    public void Create_WithNullString_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => global::Orleans.HierarchicalKey.Create(null!));
    }

    [Fact]
    public void Create_WithEmptySegment_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create("foo//bar"));
    }

    [Fact]
    public void Create_WithTrailingSeparator_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create("foo/"));
    }

    [Fact]
    public void Create_WithLeadingSeparator_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create("/foo"));
    }

    [Fact]
    public void Parse_WithValidString_CreatesKey()
    {
        var key = global::Orleans.HierarchicalKey.Parse("foo/bar", null);
        Assert.NotNull(key);
        Assert.Equal("foo/bar", key.ToString());
    }

    [Fact]
    public void TryParse_WithValidString_ReturnsTrue()
    {
        var result = global::Orleans.HierarchicalKey.TryParse("foo/bar", null, out var key);
        Assert.True(result);
        Assert.NotNull(key);
        Assert.Equal("foo/bar", key.ToString());
    }

    [Fact]
    public void TryParse_WithInvalidString_ReturnsFalse()
    {
        var result = global::Orleans.HierarchicalKey.TryParse("", null, out var key);
        Assert.False(result);
        Assert.Null(key);
    }

    [Fact]
    public void TryParse_WithEmptySegment_ReturnsFalse()
    {
        var result = global::Orleans.HierarchicalKey.TryParse("foo//bar", null, out var key);
        Assert.False(result);
        Assert.Null(key);
    }

    [Fact]
    public void CreateChildKey_CreatesChildKey()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = parent.CreateChildKey("bar");
        Assert.Equal("foo/bar", child.ToString());
    }

    [Fact]
    public void CreateChildKey_WithMultipleSegments_CreatesChildKey()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = parent.CreateChildKey("bar/baz");
        Assert.Equal("foo/bar/baz", child.ToString());
    }

    [Fact]
    public void CreateEscapedChildKey_EscapesSegmentSeparators()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = parent.CreateEscapedChildKey("bar/baz");
        Assert.Equal("foo/bar\\/baz", child.ToString());
    }

    [Fact]
    public void CreateEscaped_WithSegmentSeparator_EscapesIt()
    {
        var key = global::Orleans.HierarchicalKey.CreateEscaped("foo/bar");
        Assert.Equal("foo\\/bar", key.ToString());
    }

    [Fact]
    public void GetParent_ReturnsParentKey()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo/bar/baz");
        var parent = key.GetParent();
        Assert.NotNull(parent);
        Assert.Equal("foo/bar", parent.ToString());
    }

    [Fact]
    public void GetParent_ForSingleSegment_ReturnsNull()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo");
        var parent = key.GetParent();
        Assert.Null(parent);
    }

    [Fact]
    public void IsParentOf_WithDirectChild_ReturnsTrue()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.True(parent.IsParentOf(child));
    }

    [Fact]
    public void IsParentOf_WithGrandchild_ReturnsFalse()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var grandchild = global::Orleans.HierarchicalKey.Create("foo/bar/baz");
        Assert.False(parent.IsParentOf(grandchild));
    }

    [Fact]
    public void IsParentOf_WithSameKey_ReturnsFalse()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.False(key1.IsParentOf(key2));
    }

    [Fact]
    public void IsParentOf_WithUnrelatedKey_ReturnsFalse()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("baz/qux");
        Assert.False(key1.IsParentOf(key2));
    }

    [Fact]
    public void IsChildOf_WithDirectParent_ReturnsTrue()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.True(child.IsChildOf(parent));
    }

    [Fact]
    public void IsChildOf_WithGrandparent_ReturnsFalse()
    {
        var grandparent = global::Orleans.HierarchicalKey.Create("foo");
        var grandchild = global::Orleans.HierarchicalKey.Create("foo/bar/baz");
        Assert.False(grandchild.IsChildOf(grandparent));
    }

    [Fact]
    public void IsAncestorOf_WithDirectChild_ReturnsTrue()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.True(parent.IsAncestorOf(child));
    }

    [Fact]
    public void IsAncestorOf_WithGrandchild_ReturnsTrue()
    {
        var grandparent = global::Orleans.HierarchicalKey.Create("foo");
        var grandchild = global::Orleans.HierarchicalKey.Create("foo/bar/baz");
        Assert.True(grandparent.IsAncestorOf(grandchild));
    }

    [Fact]
    public void IsAncestorOf_WithSameKey_ReturnsTrue()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.True(key1.IsAncestorOf(key2));
    }

    [Fact]
    public void IsAncestorOf_WithUnrelatedKey_ReturnsFalse()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("baz/qux");
        Assert.False(key1.IsAncestorOf(key2));
    }

    [Fact]
    public void Equals_WithSameValue_ReturnsTrue()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.True(key1.Equals(key2));
    }

    [Fact]
    public void Equals_WithDifferentValue_ReturnsFalse()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("foo/baz");
        Assert.False(key1.Equals(key2));
    }

    [Fact]
    public void GetHashCode_WithSameValue_ReturnsSameHashCode()
    {
        var key1 = global::Orleans.HierarchicalKey.Create("foo/bar");
        var key2 = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithComposedKey_ReturnsSameHashCodeAsDirectKey()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var child = parent.CreateChildKey("bar");
        var direct = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.Equal(child.GetHashCode(), direct.GetHashCode());
    }

    [Fact]
    public void Length_ReturnsCorrectLength()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo/bar");
        Assert.Equal(7, key.Length); // "foo/bar" = 7 characters
    }

    [Fact]
    public void Length_WithEscapedCharacters_ReturnsCorrectLength()
    {
        var key = global::Orleans.HierarchicalKey.CreateEscaped("foo/bar");
        Assert.Equal(8, key.Length); // "foo\/bar" = 8 characters
    }

    [Fact]
    public void EscapeCharacter_IsBackslash()
    {
        Assert.Equal('\\', global::Orleans.HierarchicalKey.EscapeCharacter);
    }

    [Fact]
    public void SegmentSeparator_IsForwardSlash()
    {
        Assert.Equal('/', global::Orleans.HierarchicalKey.SegmentSeparator);
    }

    [Fact]
    public void Create_WithEscapedSeparator_ParsesCorrectly()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo\\/bar");
        Assert.Equal("foo\\/bar", key.ToString());
    }

    [Fact]
    public void Create_WithEscapedEscapeCharacter_ParsesCorrectly()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo\\\\bar");
        Assert.Equal("foo\\\\bar", key.ToString());
    }

    [Fact]
    public void Create_WithInvalidEscapeSequence_ThrowsArgumentException()
    {
        // Escape character must be followed by either '/' or '\'
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create("foo\\bar"));
    }

    [Fact]
    public void Create_WithIncompleteEscapeSequence_ThrowsArgumentException()
    {
        // Escape character at end of string is invalid
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create("foo\\"));
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllSegments()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo/bar/baz");
        var segments = new List<string>();
        var enumerator = key.GetEnumerator();
        while (enumerator.MoveNext())
        {
            segments.Add(enumerator.Current.ToString());
        }
        Assert.Equal(new[] { "foo", "bar", "baz" }, segments);
    }

    [Fact]
    public void GetEnumerator_WithSingleSegment_EnumeratesOneSegment()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo");
        var segments = new List<string>();
        var enumerator = key.GetEnumerator();
        while (enumerator.MoveNext())
        {
            segments.Add(enumerator.Current.ToString());
        }
        Assert.Equal(new[] { "foo" }, segments);
    }

    [Fact]
    public void GetEnumerator_WithEscapedSegment_EnumeratesEscapedSegment()
    {
        var key = global::Orleans.HierarchicalKey.Create("foo/bar\\/baz/qux");
        var segments = new List<string>();
        var enumerator = key.GetEnumerator();
        while (enumerator.MoveNext())
        {
            segments.Add(enumerator.Current.ToString());
        }
        Assert.Equal(new[] { "foo", "bar\\/baz", "qux" }, segments);
    }

    [Fact]
    public void CreateWithParent_CreatesKeyWithParent()
    {
        var parent = global::Orleans.HierarchicalKey.Create("foo");
        var key = global::Orleans.HierarchicalKey.Create(parent, "bar");
        Assert.Equal("foo/bar", key.ToString());
    }

    [Fact]
    public void CreateWithNullParent_CreatesKeyWithoutParent()
    {
        var key = global::Orleans.HierarchicalKey.Create(null, "bar");
        Assert.Equal("bar", key.ToString());
    }

    [Fact]
    public void EveryChildFactory_RejectsNullEmptyAndMalformedChildren()
    {
        var parent = global::Orleans.HierarchicalKey.Create("parent");

        Assert.Throws<ArgumentNullException>(() => global::Orleans.HierarchicalKey.Create(parent, null!));
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create(parent, ""));
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.Create(parent, "child\\"));
        Assert.Throws<ArgumentNullException>(() => parent.CreateChildKey(null!));
        Assert.Throws<ArgumentException>(() => parent.CreateChildKey(""));
        Assert.Throws<ArgumentException>(() => parent.CreateChildKey("child\\q"));
        Assert.Throws<ArgumentNullException>(() => parent.CreateEscapedChildKey(null!));
        Assert.Throws<ArgumentException>(() => parent.CreateEscapedChildKey(""));
        Assert.Throws<ArgumentNullException>(() => global::Orleans.HierarchicalKey.CreateEscaped(null!));
        Assert.Throws<ArgumentException>(() => global::Orleans.HierarchicalKey.CreateEscaped(""));
    }

    [Fact]
    public void ChildFactories_PreserveEqualityFormattingAndHashAcrossRepresentations()
    {
        var parent = global::Orleans.HierarchicalKey.Create("root");
        var composed = parent.CreateEscapedChildKey("slash/and\\escape");
        var parsed = global::Orleans.HierarchicalKey.Parse(composed.ToString(), provider: null);

        Assert.Equal(composed, parsed);
        Assert.Equal(composed.ToString(), parsed.ToString());
        Assert.Equal(composed.GetHashCode(), parsed.GetHashCode());
        Assert.True(composed.IsChildOf(parent));
    }
}
