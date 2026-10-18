using System.Buffers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public class MessageIdTests
{
    [Fact]
    public void SerializationContract_IdentifiesCurrentType()
    {
        var type = typeof(MessageId);
        Assert.Equal("Orleans.Messaging.MessageId", Assert.Single(type.GetCustomAttributes<AliasAttribute>()).Alias);
        Assert.True(type.IsValueType);
        Assert.Empty(type.GetCustomAttributes<GenerateSerializerAttribute>());
    }

    [Fact]
    public void Identity_WrapsKeyAndComparesByCanonicalPath()
    {
        var key = HierarchicalKey.Create("orders", "a/b", "reserve");
        var id = new MessageId(key);

        Assert.Equal(key, id.Key);
        Assert.False(id.IsDefault);
        Assert.Equal(id, MessageId.Create("orders", "a/b", "reserve"));
        Assert.True(id == new MessageId(HierarchicalKey.Parse(key.ToString())));
        Assert.True(id != MessageId.Create("orders", "a/b"));
        Assert.Equal(key.GetHashCode(), id.GetHashCode());
        Assert.False(id.Equals((object)key));
        Assert.True(default(MessageId).IsDefault);
        Assert.Equal(default, new MessageId(default));
    }

    [Fact]
    public void ParseAndFormat_UseCanonicalEscapedPath()
    {
        var id = MessageId.Create("orders", "a/b", @"c\d");
        const string canonical = @"orders/a\/b/c\\d";

        Assert.Equal(canonical, id.ToString());
        Assert.Equal(canonical, $"{id}");
        Assert.Equal(id, MessageId.Parse(canonical));
        Assert.Equal(id, MessageId.Parse(canonical.AsSpan()));
        Assert.True(MessageId.TryParse(canonical, null, out var parsed));
        Assert.Equal(id, parsed);
        Assert.True(MessageId.TryParse(canonical.AsSpan(), null, out parsed));
        Assert.Equal(id, parsed);
        var buffer = new char[canonical.Length];
        Assert.True(id.TryFormat(buffer, out var written, default, null));
        Assert.Equal(canonical, new string(buffer, 0, written));
        Assert.False(id.TryFormat(buffer.AsSpan(0, canonical.Length - 1), out written, default, null));
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData(@"a\q")]
    public void Parse_InvalidPath_IsRejected(string text)
    {
        Assert.False(MessageId.TryParse(text, null, out var result));
        Assert.True(result.IsDefault);
        Assert.False(MessageId.TryParse(text.AsSpan(), null, out result));
        Assert.True(result.IsDefault);
        Assert.Throws<FormatException>(() => MessageId.Parse(text));
        Assert.False(MessageId.TryParse(null, null, out result));
        Assert.True(result.IsDefault);
    }

    [Fact]
    public void Json_ValueAndDictionaryKeyRoundTripCanonicalEscapedPath()
    {
        var id = MessageId.Create("orders", "a/b", @"c\d");

        var json = JsonSerializer.Serialize(id);
        Assert.Equal(JsonSerializer.Serialize(@"orders/a\/b/c\\d"), json);
        Assert.Equal(id, JsonSerializer.Deserialize<MessageId>(json));

        var map = JsonSerializer.Serialize(new Dictionary<MessageId, int> { [id] = 42 });
        Assert.Equal(JsonSerializer.Serialize(new Dictionary<string, int> { [id.ToString()] = 42 }), map);
        Assert.Equal(42, JsonSerializer.Deserialize<Dictionary<MessageId, int>>(map)![id]);
    }

    [Fact]
    public void Json_DefaultValueRoundTripsAsNullAndIsRejectedAsDictionaryKey()
    {
        Assert.Equal("null", JsonSerializer.Serialize(default(MessageId)));
        Assert.True(JsonSerializer.Deserialize<MessageId>("null").IsDefault);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new Dictionary<MessageId, int> { [default] = 1 }));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"a//b\"")]
    [InlineData("42")]
    public void Json_MalformedValueIsRejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MessageId>(json));

    [Theory]
    [InlineData("order-{high}")]
    [InlineData("{low}-order")]
    [InlineData("order-{low}{high}")]
    public void UnpairedSurrogate_IsRejectedOnEveryConstructionPath(string template)
    {
        var text = HierarchicalKeyTests.WithUnpairedSurrogates(template);
        Assert.Throws<ArgumentException>(() => MessageId.Create(text));
        Assert.Throws<ArgumentException>(() => MessageId.Create("orders", text));
        Assert.Throws<FormatException>(() => MessageId.Parse(text));
        Assert.Throws<FormatException>(() => MessageId.Parse(text.AsSpan()));
        Assert.False(MessageId.TryParse(text, null, out var result));
        Assert.True(result.IsDefault);
        Assert.False(MessageId.TryParse(text.AsSpan(), null, out result));
        Assert.True(result.IsDefault);
    }

    [Fact]
    public void SurrogatePairs_RoundTripThroughOrleansSerialization()
    {
        using var first = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var second = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var id = MessageId.Create("order-\U0001F600");

        var decoded = second.GetRequiredService<Serializer<MessageId>>().Deserialize(first.GetRequiredService<Serializer<MessageId>>().SerializeToArray(id));

        Assert.Equal(id, decoded);
        Assert.Equal("order-\U0001F600", decoded.ToString());
        Assert.Equal(id, MessageId.Parse("order-\U0001F600"));
        Assert.NotEqual(decoded, MessageId.Create("order-\U0001F601"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializationAndCopy_PreserveIdentity(bool unset)
    {
        using var first = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var second = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var id = unset ? default : MessageId.Create("tenant", @"a\/b", "42");
        var serializer = first.GetRequiredService<Serializer<MessageId>>();
        var bytes = serializer.SerializeToArray(id);
        var decoded = second.GetRequiredService<Serializer<MessageId>>().Deserialize(bytes);

        Assert.Equal(id, decoded);
        Assert.Equal(id.IsDefault, decoded.IsDefault);
        Assert.Equal(id, first.GetRequiredService<DeepCopier>().Copy(id));
        Assert.Equal(bytes, serializer.SerializeToArray(decoded));
        var map = first.GetRequiredService<Serializer<Dictionary<MessageId, int>>>();
        var dictionary = Assert.IsType<Dictionary<MessageId, int>>(map.Deserialize(map.SerializeToArray(new() { [id] = 42 })));
        Assert.Equal(42, dictionary[decoded]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData(@"a\q")]
    public void Codec_InvalidCanonicalWire_IsRejected(string canonical)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var output = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(output, session);
        ReferenceCodec.MarkValueField(session);
        writer.WriteFieldHeader(0, typeof(MessageId), typeof(MessageId), WireType.TagDelimited);
        services.GetRequiredService<IFieldCodec<string>>().WriteField(ref writer, 0, typeof(string), canonical);
        writer.WriteEndObject();
        writer.Commit();
        Assert.Throws<FormatException>(() => services.GetRequiredService<Serializer<MessageId>>().Deserialize(output.WrittenSpan));
    }
}
