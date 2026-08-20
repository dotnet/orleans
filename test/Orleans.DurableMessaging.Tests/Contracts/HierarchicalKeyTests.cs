using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

/// <summary>
/// Tests for hierarchical message correlation keys.
/// </summary>
[TestCategory("BVT")]
public class HierarchicalKeyTests
{
    [Fact]
    public void SerializationContract_PreservesDraftTypeAlias()
    {
        var alias = Assert.Single(typeof(HierarchicalKey).GetCustomAttributes(inherit: false).OfType<AliasAttribute>());

        Assert.Equal("Orleans.HierarchicalKey", alias.Alias);
    }

    [Fact]
    public void Create_WithValidString_CreatesKey()
    {
        var key = HierarchicalKey.Create("foo");
        Assert.NotNull(key);
        Assert.Equal("foo", key.ToString());
    }

    [Fact]
    public void Create_WithMultipleSegments_CreatesKey()
    {
        var key = HierarchicalKey.Create("foo/bar/baz");
        Assert.NotNull(key);
        Assert.Equal("foo/bar/baz", key.ToString());
    }

    [Fact]
    public void Create_WithEmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(""));
    }

    [Fact]
    public void Create_WithNullString_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Create(null!));
    }

    [Fact]
    public void Create_WithEmptySegment_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("foo//bar"));
    }

    [Fact]
    public void Create_WithTrailingSeparator_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("foo/"));
    }

    [Fact]
    public void Create_WithLeadingSeparator_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("/foo"));
    }

    [Fact]
    public void Parse_WithValidString_CreatesKey()
    {
        var key = HierarchicalKey.Parse("foo/bar", null);
        Assert.NotNull(key);
        Assert.Equal("foo/bar", key.ToString());
    }

    [Fact]
    public void TryParse_WithValidString_ReturnsTrue()
    {
        var result = HierarchicalKey.TryParse("foo/bar", null, out var key);
        Assert.True(result);
        Assert.NotNull(key);
        Assert.Equal("foo/bar", key.ToString());
    }

    [Fact]
    public void TryParse_WithInvalidString_ReturnsFalse()
    {
        var result = HierarchicalKey.TryParse("", null, out var key);
        Assert.False(result);
        Assert.Null(key);
    }

    [Fact]
    public void TryParse_WithEmptySegment_ReturnsFalse()
    {
        var result = HierarchicalKey.TryParse("foo//bar", null, out var key);
        Assert.False(result);
        Assert.Null(key);
    }

    [Fact]
    public void CreateChildKey_CreatesChildKey()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = parent.CreateChildKey("bar");
        Assert.Equal("foo/bar", child.ToString());
    }

    [Fact]
    public void CreateChildKey_WithMultipleSegments_CreatesChildKey()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = parent.CreateChildKey("bar/baz");
        Assert.Equal("foo/bar/baz", child.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bar//baz")]
    [InlineData("bar\\")]
    public void CreateChildKey_WithInvalidSegments_Throws(string value)
    {
        var parent = HierarchicalKey.Create("foo");

        Assert.Throws<ArgumentException>(() => parent.CreateChildKey(value));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(parent, value));
    }

    [Fact]
    public void CreateEscapedChildKey_EscapesSegmentSeparators()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = parent.CreateEscapedChildKey("bar/baz");
        Assert.Equal("foo/bar\\/baz", child.ToString());
    }

    [Fact]
    public void CreateEscaped_WithSegmentSeparator_EscapesIt()
    {
        var key = HierarchicalKey.CreateEscaped("foo/bar");
        Assert.Equal("foo\\/bar", key.ToString());
    }

    [Fact]
    public void CreateEscaped_WithExistingEscape_StillEscapesLaterSeparators()
    {
        var key = HierarchicalKey.CreateEscaped(@"foo\/bar/baz");

        Assert.Equal(@"foo\/bar\/baz", key.ToString());
    }

    [Fact]
    public void CreateEscaped_CopiesCallerOwnedMemory()
    {
        var characters = "foo".ToCharArray();
        var key = HierarchicalKey.CreateEscaped(parent: null, characters);

        characters[0] = 'b';

        Assert.Equal("foo", key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo\\")]
    public void CreateEscaped_WithInvalidInput_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => HierarchicalKey.CreateEscaped(value));
    }

    [Fact]
    public void GetParent_ReturnsParentKey()
    {
        var key = HierarchicalKey.Create("foo/bar/baz");
        var parent = key.GetParent();
        Assert.NotNull(parent);
        Assert.Equal("foo/bar", parent.ToString());
    }

    [Fact]
    public void GetParent_ForSingleSegment_ReturnsNull()
    {
        var key = HierarchicalKey.Create("foo");
        var parent = key.GetParent();
        Assert.Null(parent);
    }

    [Fact]
    public void IsParentOf_WithDirectChild_ReturnsTrue()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = HierarchicalKey.Create("foo/bar");
        Assert.True(parent.IsParentOf(child));
    }

    [Fact]
    public void IsParentOf_WithGrandchild_ReturnsFalse()
    {
        var parent = HierarchicalKey.Create("foo");
        var grandchild = HierarchicalKey.Create("foo/bar/baz");
        Assert.False(parent.IsParentOf(grandchild));
    }

    [Fact]
    public void IsParentOf_WithSameKey_ReturnsFalse()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("foo/bar");
        Assert.False(key1.IsParentOf(key2));
    }

    [Fact]
    public void IsParentOf_WithUnrelatedKey_ReturnsFalse()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("baz/qux");
        Assert.False(key1.IsParentOf(key2));
    }

    [Fact]
    public void IsChildOf_WithDirectParent_ReturnsTrue()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = HierarchicalKey.Create("foo/bar");
        Assert.True(child.IsChildOf(parent));
    }

    [Fact]
    public void IsChildOf_WithGrandparent_ReturnsFalse()
    {
        var grandparent = HierarchicalKey.Create("foo");
        var grandchild = HierarchicalKey.Create("foo/bar/baz");
        Assert.False(grandchild.IsChildOf(grandparent));
    }

    [Fact]
    public void IsAncestorOf_WithDirectChild_ReturnsTrue()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = HierarchicalKey.Create("foo/bar");
        Assert.True(parent.IsAncestorOf(child));
    }

    [Fact]
    public void IsAncestorOf_WithGrandchild_ReturnsTrue()
    {
        var grandparent = HierarchicalKey.Create("foo");
        var grandchild = HierarchicalKey.Create("foo/bar/baz");
        Assert.True(grandparent.IsAncestorOf(grandchild));
    }

    [Fact]
    public void IsAncestorOf_WithSameKey_ReturnsTrue()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("foo/bar");
        Assert.True(key1.IsAncestorOf(key2));
    }

    [Fact]
    public void IsAncestorOf_WithUnrelatedKey_ReturnsFalse()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("baz/qux");
        Assert.False(key1.IsAncestorOf(key2));
    }

    [Fact]
    public void Equals_WithSameValue_ReturnsTrue()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("foo/bar");
        Assert.True(key1.Equals(key2));
    }

    [Fact]
    public void Equals_WithDifferentValue_ReturnsFalse()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("foo/baz");
        Assert.False(key1.Equals(key2));
    }

    [Fact]
    public void GetHashCode_WithSameValue_ReturnsSameHashCode()
    {
        var key1 = HierarchicalKey.Create("foo/bar");
        var key2 = HierarchicalKey.Create("foo/bar");
        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WithComposedKey_ReturnsSameHashCodeAsDirectKey()
    {
        var parent = HierarchicalKey.Create("foo");
        var child = parent.CreateChildKey("bar");
        var direct = HierarchicalKey.Create("foo/bar");
        Assert.Equal(child.GetHashCode(), direct.GetHashCode());
    }

    [Fact]
    public void Length_ReturnsCorrectLength()
    {
        var key = HierarchicalKey.Create("foo/bar");
        Assert.Equal(7, key.Length); // "foo/bar" = 7 characters
    }

    [Fact]
    public void Length_WithEscapedCharacters_ReturnsCorrectLength()
    {
        var key = HierarchicalKey.CreateEscaped("foo/bar");
        Assert.Equal(8, key.Length); // "foo\/bar" = 8 characters
    }

    [Fact]
    public void EscapeCharacter_IsBackslash()
    {
        Assert.Equal('\\', HierarchicalKey.EscapeCharacter);
    }

    [Fact]
    public void SegmentSeparator_IsForwardSlash()
    {
        Assert.Equal('/', HierarchicalKey.SegmentSeparator);
    }

    [Fact]
    public void Create_WithEscapedSeparator_ParsesCorrectly()
    {
        var key = HierarchicalKey.Create("foo\\/bar");
        Assert.Equal("foo\\/bar", key.ToString());
    }

    [Fact]
    public void Create_WithEscapedEscapeCharacter_ParsesCorrectly()
    {
        var key = HierarchicalKey.Create("foo\\\\bar");
        Assert.Equal("foo\\\\bar", key.ToString());
    }

    [Fact]
    public void Create_WithInvalidEscapeSequence_ThrowsArgumentException()
    {
        // Escape character must be followed by either '/' or '\'
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("foo\\bar"));
    }

    [Fact]
    public void Create_WithIncompleteEscapeSequence_ThrowsArgumentException()
    {
        // Escape character at end of string is invalid
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("foo\\"));
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllSegments()
    {
        var key = HierarchicalKey.Create("foo/bar/baz");
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
        var key = HierarchicalKey.Create("foo");
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
        var key = HierarchicalKey.Create("foo/bar\\/baz/qux");
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
        var parent = HierarchicalKey.Create("foo");
        var key = HierarchicalKey.Create(parent, "bar");
        Assert.Equal("foo/bar", key.ToString());
    }

    [Fact]
    public void CreateWithNullParent_CreatesKeyWithoutParent()
    {
        var key = HierarchicalKey.Create(null, "bar");
        Assert.Equal("bar", key.ToString());
    }
}
