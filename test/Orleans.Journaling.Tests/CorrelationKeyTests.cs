using Orleans.Journaling.Messaging;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for CorrelationKey, a hierarchical string type used for distributed message correlation.
/// CorrelationKey uses '/' as the segment separator and '\\' as the escape character,
/// allowing for hierarchical correlation across distributed operations.
/// </summary>
[TestCategory("BVT")]
public class CorrelationKeyTests
{
    /// <summary>
    /// Tests basic creation of correlation keys from strings.
    /// </summary>
    [Fact]
    public void Create_ValidString_ReturnsKey()
    {
        var key = CorrelationKey.Create("transfer-123");
        Assert.NotNull(key);
        Assert.Equal("transfer-123", key.ToString());
    }

    /// <summary>
    /// Tests that null strings throw ArgumentNullException.
    /// </summary>
    [Fact]
    public void Create_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => CorrelationKey.Create(null!));
    }

    /// <summary>
    /// Tests that empty strings throw ArgumentException.
    /// </summary>
    [Fact]
    public void Create_Empty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CorrelationKey.Create(""));
    }

    /// <summary>
    /// Tests that keys with empty segments are rejected.
    /// </summary>
    [Theory]
    [InlineData("foo//bar")]
    [InlineData("/foo")]
    [InlineData("foo/")]
    [InlineData("foo/bar//baz")]
    public void Create_EmptySegments_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => CorrelationKey.Create(value));
    }

    /// <summary>
    /// Tests that incomplete escape sequences are rejected.
    /// </summary>
    [Fact]
    public void Create_IncompleteEscapeSequence_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CorrelationKey.Create("foo\\"));
    }

    /// <summary>
    /// Tests semantic equality - keys with the same path are equal regardless of how they were constructed.
    /// </summary>
    [Fact]
    public void Equals_SemanticEquality_ReturnsTrue()
    {
        var aParent = CorrelationKey.Create("foo/bar");
        var a = aParent.CreateChildKey("baz");
        var b = CorrelationKey.Create("foo/bar/baz");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals(b));
        Assert.True(b.Equals(a));
    }

    /// <summary>
    /// Tests that different keys are not equal.
    /// </summary>
    [Fact]
    public void Equals_DifferentKeys_ReturnsFalse()
    {
        var a = CorrelationKey.Create("foo/bar");
        var b = CorrelationKey.Create("foo/baz");

        Assert.NotEqual(a, b);
        Assert.False(a.Equals(b));
    }

    /// <summary>
    /// Tests that null comparison returns false.
    /// </summary>
    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var key = CorrelationKey.Create("foo");
        Assert.False(key.Equals(null));
    }

    /// <summary>
    /// Tests escaping of segment separators.
    /// </summary>
    [Fact]
    public void CreateEscaped_SegmentSeparator_EscapesCorrectly()
    {
        var aParent = CorrelationKey.Create("foo/bar\\/");
        var a = aParent.CreateChildKey("baz");
        var b = CorrelationKey.Create("foo/bar\\//baz");

        Assert.Equal(a, b);
    }

    /// <summary>
    /// Tests escaping of backslash characters.
    /// </summary>
    [Fact]
    public void CreateEscaped_EscapeCharacter_EscapesCorrectly()
    {
        var a = CorrelationKey.Create("foo\\\\bar");
        Assert.Equal("foo\\\\bar", a.ToString());
    }

    /// <summary>
    /// Tests that CreateEscapedChildKey properly escapes segment separators.
    /// </summary>
    [Fact]
    public void CreateEscapedChildKey_WithSeparator_EscapesValue()
    {
        var parent = CorrelationKey.Create("foo/bar\\/");
        var a = parent.CreateEscapedChildKey("baz/boz");
        var b = parent.CreateEscapedChildKey("baz\\/boz");

        Assert.Equal(a, b);
    }

    /// <summary>
    /// Tests IsParentOf relationship.
    /// </summary>
    [Fact]
    public void IsParentOf_DirectChild_ReturnsTrue()
    {
        var parent = CorrelationKey.Create("aaa");
        var child = CorrelationKey.Create("aaa/bbb");

        Assert.True(parent.IsParentOf(child));
        Assert.False(parent.IsParentOf(parent));
    }

    /// <summary>
    /// Tests IsParentOf with grandchild returns false.
    /// </summary>
    [Fact]
    public void IsParentOf_Grandchild_ReturnsFalse()
    {
        var parent = CorrelationKey.Create("aaa");
        var grandchild = CorrelationKey.Create("aaa/bbb/ccc");

        Assert.False(parent.IsParentOf(grandchild));
    }

    /// <summary>
    /// Tests IsParentOf with null returns false.
    /// </summary>
    [Fact]
    public void IsParentOf_Null_ReturnsFalse()
    {
        var key = CorrelationKey.Create("aaa");
        Assert.False(key.IsParentOf(null));
    }

    /// <summary>
    /// Tests IsChildOf relationship.
    /// </summary>
    [Fact]
    public void IsChildOf_DirectParent_ReturnsTrue()
    {
        var parent = CorrelationKey.Create("aaa");
        var child = CorrelationKey.Create("aaa/bbb");

        Assert.True(child.IsChildOf(parent));
        Assert.False(parent.IsChildOf(child));
    }

    /// <summary>
    /// Tests IsChildOf with null returns false.
    /// </summary>
    [Fact]
    public void IsChildOf_Null_ReturnsFalse()
    {
        var key = CorrelationKey.Create("aaa");
        Assert.False(key.IsChildOf(null));
    }

    /// <summary>
    /// Tests IsAncestorOf with descendants.
    /// </summary>
    [Fact]
    public void IsAncestorOf_Descendants_ReturnsTrue()
    {
        var ancestor = CorrelationKey.Create("aaa");
        var child = CorrelationKey.Create("aaa/bbb");
        var grandchild = CorrelationKey.Create("aaa/bbb/ccc");

        Assert.True(ancestor.IsAncestorOf(child));
        Assert.True(ancestor.IsAncestorOf(grandchild));
        Assert.True(child.IsAncestorOf(grandchild));
    }

    /// <summary>
    /// Tests IsAncestorOf with self returns true.
    /// </summary>
    [Fact]
    public void IsAncestorOf_Self_ReturnsTrue()
    {
        var key = CorrelationKey.Create("aaa");
        Assert.True(key.IsAncestorOf(key));
    }

    /// <summary>
    /// Tests IsAncestorOf with unrelated key returns false.
    /// </summary>
    [Fact]
    public void IsAncestorOf_Unrelated_ReturnsFalse()
    {
        var a = CorrelationKey.Create("aaa");
        var b = CorrelationKey.Create("bbb");

        Assert.False(a.IsAncestorOf(b));
        Assert.False(b.IsAncestorOf(a));
    }

    /// <summary>
    /// Tests IsAncestorOf with null returns false.
    /// </summary>
    [Fact]
    public void IsAncestorOf_Null_ReturnsFalse()
    {
        var key = CorrelationKey.Create("aaa");
        Assert.False(key.IsAncestorOf(null));
    }

    /// <summary>
    /// Tests GetParent with single-segment key.
    /// </summary>
    [Fact]
    public void GetParent_SingleSegment_ReturnsNull()
    {
        var key = CorrelationKey.Create("foo");
        Assert.Null(key.GetParent());
    }

    /// <summary>
    /// Tests GetParent with multi-segment key.
    /// </summary>
    [Fact]
    public void GetParent_MultiSegment_ReturnsParent()
    {
        var key = CorrelationKey.Create("foo/bar/baz");
        var parent = key.GetParent();

        Assert.NotNull(parent);
        Assert.Equal("foo/bar", parent.ToString());

        var grandparent = parent.GetParent();
        Assert.NotNull(grandparent);
        Assert.Equal("foo", grandparent.ToString());

        var greatGrandparent = grandparent.GetParent();
        Assert.Null(greatGrandparent);
    }

    /// <summary>
    /// Tests Parse with valid input.
    /// </summary>
    [Fact]
    public void Parse_ValidString_ReturnsKey()
    {
        var key = CorrelationKey.Parse("foo/bar", null);
        Assert.Equal("foo/bar", key.ToString());
    }

    /// <summary>
    /// Tests Parse with invalid input throws exception.
    /// </summary>
    [Fact]
    public void Parse_InvalidString_ThrowsException()
    {
        Assert.Throws<ArgumentException>(() => CorrelationKey.Parse("foo//bar", null));
    }

    /// <summary>
    /// Tests TryParse with valid input.
    /// </summary>
    [Fact]
    public void TryParse_ValidString_ReturnsTrue()
    {
        var success = CorrelationKey.TryParse("foo/bar", null, out var key);
        Assert.True(success);
        Assert.NotNull(key);
        Assert.Equal("foo/bar", key.ToString());
    }

    /// <summary>
    /// Tests TryParse with invalid input.
    /// </summary>
    [Theory]
    [InlineData("foo//bar")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_InvalidString_ReturnsFalse(string? value)
    {
        var success = CorrelationKey.TryParse(value, null, out var key);
        Assert.False(success);
        Assert.Null(key);
    }

    /// <summary>
    /// Tests Parse with ReadOnlySpan.
    /// </summary>
    [Fact]
    public void Parse_Span_ReturnsKey()
    {
        ReadOnlySpan<char> span = "foo/bar".AsSpan();
        var key = CorrelationKey.Parse(span, null);
        Assert.Equal("foo/bar", key.ToString());
    }

    /// <summary>
    /// Tests TryParse with ReadOnlySpan.
    /// </summary>
    [Fact]
    public void TryParse_Span_ReturnsTrue()
    {
        ReadOnlySpan<char> span = "foo/bar".AsSpan();
        var success = CorrelationKey.TryParse(span, null, out var key);
        Assert.True(success);
        Assert.NotNull(key);
        Assert.Equal("foo/bar", key.ToString());
    }

    /// <summary>
    /// Tests TryFormat with sufficient buffer.
    /// </summary>
    [Fact]
    public void TryFormat_SufficientBuffer_ReturnsTrue()
    {
        var key = CorrelationKey.Create("foo/bar/baz");
        Span<char> buffer = stackalloc char[100];
        var success = key.TryFormat(buffer, out var charsWritten, ReadOnlySpan<char>.Empty, null);

        Assert.True(success);
        Assert.Equal("foo/bar/baz", buffer[..charsWritten].ToString());
    }

    /// <summary>
    /// Tests TryFormat with insufficient buffer.
    /// </summary>
    [Fact]
    public void TryFormat_InsufficientBuffer_ReturnsFalse()
    {
        var key = CorrelationKey.Create("foo/bar/baz");
        Span<char> buffer = stackalloc char[5];
        var success = key.TryFormat(buffer, out var charsWritten, ReadOnlySpan<char>.Empty, null);

        Assert.False(success);
    }

    /// <summary>
    /// Tests segment enumeration.
    /// </summary>
    [Fact]
    public void SegmentEnumerator_MultipleSegments_EnumeratesCorrectly()
    {
        var key = CorrelationKey.Create("foo/bar/baz");
        var enumerator = key.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal("foo", enumerator.Current.ToString());

        Assert.True(enumerator.MoveNext());
        Assert.Equal("bar", enumerator.Current.ToString());

        Assert.True(enumerator.MoveNext());
        Assert.Equal("baz", enumerator.Current.ToString());

        Assert.False(enumerator.MoveNext());
    }

    /// <summary>
    /// Tests segment enumeration with escaped separators.
    /// </summary>
    [Fact]
    public void SegmentEnumerator_EscapedSeparators_EnumeratesCorrectly()
    {
        var key = CorrelationKey.Create("foo/bar\\/baz/qux");
        var enumerator = key.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal("foo", enumerator.Current.ToString());

        Assert.True(enumerator.MoveNext());
        Assert.Equal("bar\\/baz", enumerator.Current.ToString());

        Assert.True(enumerator.MoveNext());
        Assert.Equal("qux", enumerator.Current.ToString());

        Assert.False(enumerator.MoveNext());
    }

    /// <summary>
    /// Tests Length property.
    /// </summary>
    [Fact]
    public void Length_ReturnsCorrectValue()
    {
        var key = CorrelationKey.Create("foo/bar/baz");
        Assert.Equal(11, key.Length); // "foo/bar/baz"
    }

    /// <summary>
    /// Tests Length property with escaped characters.
    /// </summary>
    [Fact]
    public void Length_WithEscapedCharacters_ReturnsCorrectValue()
    {
        var key = CorrelationKey.Create("foo/bar\\/baz");
        Assert.Equal(12, key.Length); // "foo/bar\/baz"
    }

    /// <summary>
    /// Tests CreateChildKey creates correct hierarchy.
    /// </summary>
    [Fact]
    public void CreateChildKey_CreatesCorrectHierarchy()
    {
        var parent = CorrelationKey.Create("transfer-123");
        var debit = parent.CreateChildKey("debit");
        var credit = parent.CreateChildKey("credit");

        Assert.Equal("transfer-123/debit", debit.ToString());
        Assert.Equal("transfer-123/credit", credit.ToString());
        Assert.True(parent.IsParentOf(debit));
        Assert.True(parent.IsParentOf(credit));
    }

    /// <summary>
    /// Tests complex hierarchy with multiple levels.
    /// </summary>
    [Fact]
    public void ComplexHierarchy_Multiplelevels_WorksCorrectly()
    {
        var workflow = CorrelationKey.Create("workflow-456");
        var step1 = workflow.CreateChildKey("step1");
        var substep1a = step1.CreateChildKey("substep1a");
        var substep1b = step1.CreateChildKey("substep1b");

        Assert.Equal("workflow-456/step1/substep1a", substep1a.ToString());
        Assert.Equal("workflow-456/step1/substep1b", substep1b.ToString());
        Assert.True(workflow.IsAncestorOf(substep1a));
        Assert.True(step1.IsParentOf(substep1a));
        Assert.False(workflow.IsParentOf(substep1a));
    }

    /// <summary>
    /// Tests that invalid escape sequences are rejected.
    /// </summary>
    [Theory]
    [InlineData("foo\\bar")]  // Backslash not escaping a valid character
    [InlineData("foo\\x")]    // Backslash escaping an invalid character
    public void Create_InvalidEscapeSequence_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => CorrelationKey.Create(value));
    }

    /// <summary>
    /// Tests ToString format provider overload.
    /// </summary>
    [Fact]
    public void ToString_WithFormatProvider_ReturnsString()
    {
        var key = CorrelationKey.Create("foo/bar");
        var result = key.ToString(null, null);
        Assert.Equal("foo/bar", result);
    }

    /// <summary>
    /// Tests CreateEscaped static method.
    /// </summary>
    [Fact]
    public void CreateEscaped_WithSeparators_EscapesCorrectly()
    {
        var key = CorrelationKey.CreateEscaped("foo/bar");
        Assert.Equal("foo\\/bar", key.ToString());
    }

    /// <summary>
    /// Tests object.Equals override.
    /// </summary>
    [Fact]
    public void ObjectEquals_WithMatchingKey_ReturnsTrue()
    {
        var a = CorrelationKey.Create("foo/bar");
        object b = CorrelationKey.Create("foo/bar");

        Assert.True(a.Equals(b));
    }

    /// <summary>
    /// Tests object.Equals with non-CorrelationKey object.
    /// </summary>
    [Fact]
    public void ObjectEquals_WithDifferentType_ReturnsFalse()
    {
        var key = CorrelationKey.Create("foo/bar");
        object other = "foo/bar";

        Assert.False(key.Equals(other));
    }
}
