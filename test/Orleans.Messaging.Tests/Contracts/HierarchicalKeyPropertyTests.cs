using System.Text;
using System.Text.Json;
using CsCheck;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

/// <summary>
/// Checks the identity laws on which deduplication relies against an independent model: a key is the list of its
/// literal segments.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class HierarchicalKeyPropertyTests
{
    // Characters which exercise escaping, multi-byte UTF-8, and surrogate pairs.
    private static readonly Gen<string> Atom = Gen.OneOfConst("a", "b", "/", "\\", "\\/", "é", "😀", " ", "\0");

    private static readonly Gen<string> Segment = Atom.Array[1, 6].Select(static parts => string.Concat(parts));

    private static readonly Gen<string[]> Segments = Segment.Array[1, 5];

    private static readonly Gen<char> LoneSurrogate = Gen.OneOf(Gen.Char['\uD800', '\uDBFF'], Gen.Char['\uDC00', '\uDFFF']);

    [Fact]
    public void FormatAndParse_RoundTripEveryCreatedKey() =>
        Segments.Sample(static segments =>
        {
            var key = HierarchicalKey.Create(segments);
            Assert.Equal(key, HierarchicalKey.Parse(key.ToString()));
            Assert.Equal(new MessageId(key), MessageId.Parse(key.ToString()));
        });

    [Fact]
    public void Create_IsInjective() =>
        Gen.Select(Segments, Segments).Sample(static (left, right) =>
        {
            var leftKey = HierarchicalKey.Create(left);
            var rightKey = HierarchicalKey.Create(right);
            Assert.Equal(left.AsSpan().SequenceEqual(right), leftKey == rightKey);
            Assert.Equal(leftKey == rightKey, new MessageId(leftKey) == new MessageId(rightKey));
            if (leftKey == rightKey) Assert.Equal(leftKey.GetHashCode(), rightKey.GetHashCode());
        });

    [Fact]
    public void Segments_UnescapeToTheLiteralSegments() =>
        Segments.Sample(static segments =>
        {
            var key = HierarchicalKey.Create(segments);
            var actual = new List<string>();
            foreach (var segment in key) actual.Add(Unescape(segment));
            Assert.Equal(segments, actual);
            Assert.Equal(segments.Length, actual.Count);
        });

    [Fact]
    public void Parse_AcceptsExactlyTheCanonicalForms() =>
        Gen.OneOfConst("a", "/", "\\", "\\/", "\\\\", "😀").Array[0, 8].Select(static parts => string.Concat(parts)).Sample(static text =>
        {
            var parsed = HierarchicalKey.TryParse(text, null, out var key);
            var canonical = TryModelParse(text, out var segments);
            Assert.Equal(canonical, parsed);
            if (parsed) Assert.Equal(HierarchicalKey.Create(segments.ToArray()), key);
        });

    [Fact]
    public void Navigation_MatchesTheSegmentListModel() =>
        Gen.Select(Segments, Segments).Sample(static (left, right) =>
        {
            var leftKey = HierarchicalKey.Create(left);
            var rightKey = HierarchicalKey.Create(right);
            var isPrefix = left.Length <= right.Length && right.AsSpan(0, left.Length).SequenceEqual(left);
            Assert.Equal(isPrefix, leftKey.IsAncestorOf(rightKey));
            Assert.Equal(isPrefix && right.Length == left.Length + 1, leftKey.IsParentOf(rightKey));
            Assert.Equal(isPrefix && right.Length == left.Length + 1, rightKey.IsChildOf(leftKey));
            Assert.Equal(HierarchicalKey.Create([.. left, .. right]), leftKey.Append(rightKey));
            Assert.Equal(HierarchicalKey.Create([.. left, right[0]]), leftKey.CreateChildKey(right[0]));
            Assert.Equal(left.Length == 1 ? null : HierarchicalKey.Create(left[..^1]), leftKey.GetParent());
        });

    [Fact]
    public void Create_RejectsUnpairedSurrogates() =>
        Gen.Select(Segment, LoneSurrogate, Gen.Int[0, 6]).Sample(static (segment, surrogate, position) =>
        {
            var index = Math.Min(position, segment.Length);
            if (index < segment.Length && char.IsLowSurrogate(segment[index])) index++;
            var invalid = segment.Insert(index, surrogate.ToString());
            Assert.False(IsWellFormed(invalid));
            Assert.Throws<ArgumentException>(() => HierarchicalKey.Create(invalid));
            Assert.Throws<ArgumentException>(() => MessageId.Create("root", invalid));
        });

    [Fact]
    public void Json_RoundTripsKeysAndMessageIds() =>
        Segments.Sample(static segments =>
        {
            var id = MessageId.Create(segments);
            var json = JsonSerializer.Serialize(id);
            Assert.Equal(JsonSerializer.Serialize(id.ToString()), json);
            Assert.Equal(id, JsonSerializer.Deserialize<MessageId>(json));
            Assert.Equal(id.Key, JsonSerializer.Deserialize<HierarchicalKey>(json));
        });

    private static bool IsWellFormed(string value)

    {
        try
        {
            _ = new UTF8Encoding(false, true).GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    // An independent decoder of the canonical form: '\' escapes exactly '\' or '/', and '/' separates nonempty segments.
    private static bool TryModelParse(string text, out List<string> segments)
    {
        segments = [];
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                if (++i == text.Length || text[i] is not ('\\' or '/')) return false;
                current.Append(text[i]);
            }
            else if (c == '/')
            {
                if (current.Length == 0) return false;
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length == 0) return false;
        segments.Add(current.ToString());
        return true;
    }

    private static string Unescape(ReadOnlySpan<char> segment)
    {
        var builder = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            builder.Append(segment[i] == '\\' ? segment[++i] : segment[i]);
        }

        return builder.ToString();
    }
}