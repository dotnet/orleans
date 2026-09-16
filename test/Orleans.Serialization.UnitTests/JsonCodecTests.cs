using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.TestKit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class JsonCodecTests : IDisposable
{
    private readonly TrackingConverter _converter = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly Serializer _serializer;

    public JsonCodecTests()
    {
        _serviceProvider = new ServiceCollection()
            .AddSerializer(builder => builder.AddJsonSerializer(
                isSerializable: type => type == typeof(JsonPayload) || type == typeof(TrackedValue),
                isCopyable: null,
                configureOptions: optionsBuilder => optionsBuilder.Configure(options =>
                {
                    options.SerializerOptions.NumberHandling = JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString;
                    options.SerializerOptions.Converters.Add(_converter);
                    options.ReaderOptions = new JsonReaderOptions { MaxDepth = 8 };
                })))
            .BuildServiceProvider();
        _serializer = _serviceProvider.GetRequiredService<Serializer>();
    }

    public void Dispose() => _serviceProvider.Dispose();

    [Theory]
    [InlineData("Span", 32)]
    [InlineData("Span", 8192)]
    [InlineData("Sequence", 32)]
    [InlineData("Sequence", 8192)]
    [InlineData("Segmented", 32)]
    [InlineData("Segmented", 8192)]
    [InlineData("Stream", 32)]
    [InlineData("Stream", 8192)]
    public void ReadValue_PreservesValuesOptionsAndReferences(string input, int textLength)
    {
        var original = new JsonPayload { Number = -12345, Text = new string('x', textLength) + "\"\n\u00e9" };
        object?[] values = [original, original, null, 42];

        var result = Deserialize<object?[]>(_serializer.SerializeToArray(values), input);

        Assert.NotNull(result);
        Assert.Equal(4, result.Length);
        var payload = Assert.IsType<JsonPayload>(result[0]);
        Assert.NotSame(original, payload);
        Assert.Equal(original.Number, payload.Number);
        Assert.Equal(original.Text, payload.Text);
        Assert.Same(payload, result[1]);
        Assert.Null(result[2]);
        Assert.Equal(42, Assert.IsType<int>(result[3]));
    }

    [Theory]
    [InlineData("Span")]
    [InlineData("Sequence")]
    [InlineData("Segmented")]
    [InlineData("Stream")]
    public void ReadValue_PreservesJsonNodes(string input)
    {
        JsonNode?[] values =
        [
            JsonNode.Parse("{\"items\":[1,true,null],\"text\":\"hello\"}"),
            JsonNode.Parse("[1,true,\"three\"]"),
            JsonValue.Create("value"),
            null
        ];

        var result = Deserialize<JsonNode?[]>(_serializer.SerializeToArray(values), input);

        Assert.NotNull(result);
        Assert.Equal(values.Length, result.Length);
        Assert.IsType<JsonObject>(result[0]);
        Assert.IsType<JsonArray>(result[1]);
        Assert.IsAssignableFrom<JsonValue>(result[2]);
        Assert.Null(result[3]);
        for (var i = 0; i < values.Length; i++)
        {
            Assert.True(JsonNode.DeepEquals(values[i], result[i]));
        }
    }

    [Theory]
    [InlineData("Span")]
    [InlineData("Sequence")]
    [InlineData("Segmented")]
    [InlineData("Stream")]
    public void ReadValue_HonorsReaderOptions(string input)
    {
        var value = JsonNode.Parse(new string('[', 9) + "0" + new string(']', 9));
        var bytes = _serializer.SerializeToArray(value);

        Assert.ThrowsAny<JsonException>(() => Deserialize<JsonNode>(bytes, input));
    }

    [Theory]
    [InlineData("Span", 32)]
    [InlineData("Span", 8192)]
    [InlineData("Sequence", 32)]
    [InlineData("Sequence", 8192)]
    public void ReadValue_ContiguousPayloadUsesInputBuffer(string input, int textLength)
    {
        var original = new TrackedValue(new string('x', textLength));
        var bytes = _serializer.SerializeToArray(original);
        _converter.Input = bytes;

        var result = Deserialize<TrackedValue>(bytes, input);

        Assert.NotNull(result);
        Assert.Equal(original.Text, result.Text);
        Assert.True(_converter.ReadFromInput);
    }

    private T? Deserialize<T>(byte[] bytes, string input)
    {
        switch (input)
        {
            case "Span":
                return _serializer.Deserialize<T>(bytes.AsSpan());
            case "Sequence":
                return _serializer.Deserialize<T>(new ReadOnlySequence<byte>(bytes));
            case "Segmented":
                var buffer = new TestMultiSegmentBufferWriter(bytes.Length);
                buffer.Write(bytes);
                var sequence = buffer.GetReadOnlySequence(maxSegmentSize: 7);
                Assert.False(sequence.IsSingleSegment);
                return _serializer.Deserialize<T>(sequence);
            case "Stream":
                using (var stream = new MemoryStream(bytes))
                {
                    return _serializer.Deserialize<T>(stream);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown input kind.");
        }
    }

    public sealed class JsonPayload
    {
        public int Number { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public sealed record TrackedValue(string Text);

    private sealed class TrackingConverter : JsonConverter<TrackedValue>
    {
        public byte[] Input { get; set; } = [];
        public bool ReadFromInput { get; private set; }

        public override TrackedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ReadFromInput = reader.ValueSpan.Overlaps(Input);
            return new TrackedValue(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, TrackedValue value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Text);
    }
}
