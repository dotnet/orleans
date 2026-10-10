using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public class HierarchicalKeyTests
{
    [Fact]
    public void SerializationContract_IdentifiesCurrentType()
    {
        var type = typeof(HierarchicalKey);
        Assert.Equal("Orleans.DurableMessaging.HierarchicalKey", Assert.Single(type.GetCustomAttributes<AliasAttribute>()).Alias);
        Assert.True(type.IsValueType);
        Assert.Single(type.GetCustomAttributes<IsReadOnlyAttribute>());
        Assert.Empty(type.GetCustomAttributes<GenerateSerializerAttribute>());
        var field = Assert.Single(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(field.IsInitOnly);
        Assert.True(field.FieldType.IsSealed);
        Assert.Equal(typeof(string), field.FieldType.GetProperty("Canonical")!.PropertyType);
        Assert.Equal(typeof(int), field.FieldType.GetProperty("Hash")!.PropertyType);
        Assert.DoesNotContain(field.FieldType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            value => value.FieldType == typeof(HierarchicalKey) || value.FieldType == field.FieldType);
    }

    [Fact]
    public void Default_IsDistinguishedUnsetValue()
    {
        var key = default(HierarchicalKey);
        Assert.True(key.IsDefault);
        Assert.Equal(0, key.Length);
        Assert.Equal(0, key.SegmentCount);
        Assert.Equal(0, key.GetHashCode());
        Assert.Equal(string.Empty, key.ToString());
        Assert.True(key == default);
        Assert.False(key != default);
        Assert.False(key.Equals(null));
        Assert.Null(key.GetParent());
        Assert.False(key.GetEnumerator().MoveNext());
        Assert.False(key.IsAncestorOf(default));
        Assert.False(key.IsParentOf(default));
        Assert.False(key.IsChildOf(default));
        Assert.False(HierarchicalKey.Create("a").IsAncestorOf(key));
        Assert.Throws<InvalidOperationException>(() => key.CreateChildKey("a"));
        Assert.Throws<InvalidOperationException>(() => key.Append(HierarchicalKey.Create("a")));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create("a").Append(default));
        Span<char> empty = [];
        Assert.True(key.TryFormat(empty, out var written, default, null));
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("foo/bar", @"foo\/bar")]
    [InlineData(@"foo\bar", @"foo\\bar")]
    [InlineData(@"foo\/bar/baz", @"foo\\\/bar\/baz")]
    [InlineData("/", @"\/")]
    [InlineData(@"\", @"\\")]
    [InlineData("/a//b/", @"\/a\/\/b\/")]
    [InlineData(" ", " ")]
    public void Create_LiteralSegment_EscapesExactlyOnce(string literal, string canonical)
    {
        var key = HierarchicalKey.Create(literal);
        Assert.Equal(canonical, key.ToString());
        Assert.Equal(canonical.Length, key.Length);
        Assert.Equal(1, key.SegmentCount);
        Assert.Null(key.GetParent());
        Assert.Equal(key, HierarchicalKey.Parse(canonical));
        Assert.Equal([canonical], Segments(key));
        Assert.Equal(key, HierarchicalKey.Create(new[] { literal }));
    }

    [Fact]
    public void Create_WithValues_CreatesRootFirstHierarchy()
    {
        var key = HierarchicalKey.Create("orders", "42", "payment");
        var root = HierarchicalKey.Create("orders");
        var parent = root.CreateChildKey("42");
        Assert.Equal("orders/42/payment", key.ToString());
        Assert.Equal(["orders", "42", "payment"], Segments(key));
        Assert.Equal(3, key.SegmentCount);
        Assert.Equal(17, key.Length);
        Assert.Equal(parent.CreateChildKey("payment"), key);
        Assert.Equal(HierarchicalKey.Parse("orders/42/payment"), key);
        Assert.Equal(parent, key.GetParent());
        Assert.Equal(root, key.GetParent()!.Value.GetParent());
        Assert.Null(key.GetParent()!.Value.GetParent()!.Value.GetParent());
        Assert.True(parent.IsParentOf(key));
        Assert.True(key.IsChildOf(parent));
        Assert.True(root.IsAncestorOf(key));
        Assert.False(root.IsParentOf(key));
    }

    [Fact]
    public void Create_WithValues_ArrayAndSpanSlicesUseOnlyProvidedFragments()
    {
        string[] input = ["ignored", "orders", "42", "payment", "ignored"];
        var expected = HierarchicalKey.Parse("orders/42/payment");
        Assert.Equal(expected, HierarchicalKey.Create(input.AsSpan(1, 3)));
        ReadOnlySpan<string> slice = input.AsSpan(1, 3);
        Assert.Equal(expected, HierarchicalKey.Create(slice));
        Assert.Equal(expected, HierarchicalKey.Create(["orders", "42", "payment"]));
    }

    [Fact]
    public void Create_WithValues_ArrayMutationDoesNotChangeKey()
    {
        string[] input = ["orders", "42", "payment"];
        var key = HierarchicalKey.Create(input);
        var hash = key.GetHashCode();
        input[0] = "other";
        input[1] = null!;
        Assert.Equal("orders/42/payment", key.ToString());
        Assert.Equal(hash, key.GetHashCode());
        Assert.Equal(HierarchicalKey.Parse("orders/42"), key.GetParent());
    }

    [Fact]
    public void Create_WithValues_EmptyInputThrowsArgumentException()
    {
        Assert.Equal("values", Assert.Throws<ArgumentException>(() => HierarchicalKey.Create()).ParamName);
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(ReadOnlySpan<string>.Empty));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_WithValues_NullFragmentThrowsArgumentNullException(int position)
    {
        string[] input = ["a", "b", "c"];
        input[position] = null!;
        Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Create(input)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_WithValues_EmptyLiteralSegmentRejects(int position)
    {
        string[] input = ["a", "b", "c"];
        input[position] = "";
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(input)).ParamName);
    }

    [Fact]
    public void Create_LiteralSegment_RejectsOnlyNullAndEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Create((string)null!));
        Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(""));
        var parent = HierarchicalKey.Create("root");
        Assert.Throws<ArgumentNullException>(() => parent.CreateChildKey(null!));
        Assert.Throws<ArgumentException>(() => parent.CreateChildKey(""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData(@"a\")]
    [InlineData(@"a\q")]
    [InlineData(@"a\/b/")]
    public void Parse_InvalidCanonicalPath_RejectsStringAndSpan(string path)
    {
        Assert.Throws<FormatException>(() => HierarchicalKey.Parse(path));
        Assert.Throws<FormatException>(() => HierarchicalKey.Parse(path.AsSpan()));
        Assert.False(HierarchicalKey.TryParse(path, null, out var first));
        Assert.True(first.IsDefault);
        Assert.False(HierarchicalKey.TryParse(path.AsSpan(), null, out var second));
        Assert.True(second.IsDefault);
    }

    [Fact]
    public void Parse_WithNullString_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HierarchicalKey.Parse((string)null!));
        Assert.False(HierarchicalKey.TryParse(null, null, out var key));
        Assert.True(key.IsDefault);
    }

    [Theory]
    [InlineData("a/b/c", 3)]
    [InlineData(@"a\/b/c", 2)]
    [InlineData(@"a\\/b/c", 3)]
    [InlineData(@"a\\\/b/c", 2)]
    public void Parse_CanonicalEscapes_PreserveSegmentBoundaries(string path, int count)
    {
        var key = HierarchicalKey.Parse(path);
        Assert.Equal(path, key.ToString());
        Assert.Equal(count, key.SegmentCount);
        Assert.Equal(count, Segments(key).Count);
        Assert.True(HierarchicalKey.TryParse(path.AsSpan(), null, out var copy));
        Assert.True(key == copy);
        Assert.Equal(key.GetHashCode(), copy.GetHashCode());
        var characters = path.ToCharArray();
        var fromCaller = HierarchicalKey.Parse(characters.AsSpan());
        characters[0] = 'z';
        Assert.Equal(key, fromCaller);
    }

    [Fact]
    public void Append_ComposesConstructedPaths_ChildAppendsOneLiteral()
    {
        var parent = HierarchicalKey.Parse(@"root/fo\/o");
        var suffix = HierarchicalKey.Parse(@"ba\\r/baz");
        var composed = parent.Append(suffix);
        Assert.Equal(@"root/fo\/o/ba\\r/baz", composed.ToString());
        Assert.Equal(4, composed.SegmentCount);
        Assert.Equal(["root", @"fo\/o", @"ba\\r", "baz"], Segments(composed));
        Assert.Equal(parent.Append(HierarchicalKey.Create(@"ba\r")), composed.GetParent());
        Assert.True(parent.IsAncestorOf(composed));
        Assert.False(parent.IsParentOf(composed));
        var literal = parent.CreateChildKey("ba/r/baz");
        Assert.Equal(@"root/fo\/o/ba\/r\/baz", literal.ToString());
        Assert.Equal(3, literal.SegmentCount);
        Assert.Equal(parent, literal.GetParent());
        Assert.True(parent.IsParentOf(literal));
        Assert.NotEqual(composed, literal);
    }

    [Theory]
    [InlineData("foo", "foo/bar", true, true)]
    [InlineData("foo", "foo/bar/baz", false, true)]
    [InlineData("foo/bar", "foo/bar", false, true)]
    [InlineData("foo", "foobar/child", false, false)]
    [InlineData("foo", @"foo\/bar/child", false, false)]
    [InlineData("foo/bar", "foo/baz", false, false)]
    [InlineData("Foo", "foo/bar", false, false)]
    public void Navigation_UsesExactOrdinalSegments(string root, string path, bool immediate, bool ancestor)
    {
        var parent = HierarchicalKey.Parse(root);
        var child = HierarchicalKey.Parse(path);
        Assert.Equal(immediate, parent.IsParentOf(child));
        Assert.Equal(immediate, child.IsChildOf(parent));
        Assert.Equal(ancestor, parent.IsAncestorOf(child));
        if (immediate) Assert.Equal(parent, child.GetParent());
    }

    [Fact]
    public void Equality_HashOperatorsAndDictionary_UseCanonicalOrdinalIdentity()
    {
        var direct = HierarchicalKey.Parse(@"tenant/acme/orders\/42");
        var composed = HierarchicalKey.Create("tenant", "acme", "orders/42");
        Assert.True(direct == composed);
        Assert.False(direct != composed);
        Assert.True(direct.Equals((object)composed));
        Assert.Equal(direct.GetHashCode(), composed.GetHashCode());
        Assert.Equal(direct.ToString().GetHashCode(StringComparison.Ordinal), direct.GetHashCode());
        var other = HierarchicalKey.Create("Tenant", "acme", "orders/42");
        Assert.True(other != direct);
        Assert.False(other == direct);
        Assert.False(direct.Equals("tenant/acme/orders\\/42"));
        var dictionary = new Dictionary<HierarchicalKey, int> { [direct] = 42, [other] = 81, [default] = 5 };
        Assert.Equal(42, dictionary[composed]);
        Assert.Equal(81, dictionary[other]);
        Assert.Equal(5, dictionary[default]);
        Assert.Equal(3, dictionary.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializationAndCopy_RebuildCanonicalCacheAndPreserveValue(bool unset)
    {
        using var first = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var second = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var key = unset ? default : HierarchicalKey.Create("tenant", @"a\/b", "42");
        var serializer = first.GetRequiredService<Serializer<HierarchicalKey>>();
        var bytes = serializer.SerializeToArray(key);
        var decoded = second.GetRequiredService<Serializer<HierarchicalKey>>().Deserialize(bytes);
        var copy = first.GetRequiredService<DeepCopier>().Copy(key);
        Assert.Equal(key, decoded);
        Assert.Equal(key.IsDefault, decoded.IsDefault);
        Assert.Equal(key.GetHashCode(), decoded.GetHashCode());
        Assert.Equal(key.SegmentCount, decoded.SegmentCount);
        Assert.Equal(key, copy);
        var data = Assert.Single(typeof(HierarchicalKey).GetFields(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Same(data.GetValue(key), data.GetValue(copy));
        if (!unset) Assert.NotSame(data.GetValue(key), data.GetValue(decoded));
        Assert.Equal(bytes, serializer.SerializeToArray(decoded));
        var map = first.GetRequiredService<Serializer<Dictionary<HierarchicalKey, int>>>();
        var dictionary = Assert.IsType<Dictionary<HierarchicalKey, int>>(map.Deserialize(map.SerializeToArray(new() { [key] = 42 })));
        Assert.Equal(42, dictionary[decoded]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData(@"a\q")]
    public void Codec_InvalidCanonicalWire_RejectsInsteadOfConstructingKey(string canonical)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var output = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(output, session);
        ReferenceCodec.MarkValueField(session);
        writer.WriteFieldHeader(0, typeof(HierarchicalKey), typeof(HierarchicalKey), WireType.TagDelimited);
        services.GetRequiredService<IFieldCodec<string>>().WriteField(ref writer, 0, typeof(string), canonical);
        writer.WriteEndObject();
        writer.Commit();
        Assert.Throws<FormatException>(() => services.GetRequiredService<Serializer<HierarchicalKey>>().Deserialize(output.WrittenSpan));
    }

    [Fact]
    public void LongHierarchy_ConstructionNavigationAndFormatting_AreIterative()
    {
        var key = HierarchicalKey.Create(Enumerable.Repeat("x", 10000).ToArray());
        Assert.Equal(10000, key.SegmentCount);
        Assert.Equal(19999, key.Length);
        Assert.Equal(10000, Segments(key).Count);
        Assert.Equal(9999, key.GetParent()!.Value.SegmentCount);
        Assert.Equal(key, HierarchicalKey.Parse(key.ToString()));
        Assert.True(HierarchicalKey.Create("x").IsAncestorOf(key));
        var span = new char[key.Length];
        Assert.True(key.TryFormat(span, out var written, default, null));
        Assert.Equal(key.Length, written);
        Assert.Equal(key.ToString(), new string(span));
        Array.Fill(span, 'z');
        Assert.False(key.TryFormat(span.AsSpan(0, key.Length - 1), out written, default, null));
        Assert.Equal(0, written);
        Assert.All(span, character => Assert.Equal('z', character));
    }

    [Fact]
    public void EscapeCharacter_IsBackslash() => Assert.Equal('\\', HierarchicalKey.EscapeCharacter);

    [Fact]
    public void SegmentSeparator_IsForwardSlash() => Assert.Equal('/', HierarchicalKey.SegmentSeparator);

    private static List<string> Segments(HierarchicalKey key)
    {
        var values = new List<string>();
        foreach (var segment in key) values.Add(segment.ToString());
        return values;
    }
}
