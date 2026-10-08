using System.Buffers;
using System.Text;
using System.Text.Json;
using NATS.Client.Serializers.Json;
using Orleans.Runtime;
using Orleans.Streaming.NATS;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public sealed class NatsStreamMessageTests : IClassFixture<TestEnvironmentFixture>
{
    private const string ContextKey = "value";
    private readonly TestEnvironmentFixture _fixture;

    public NatsStreamMessageTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> RequestContextValues()
    {
        yield return [true];
        yield return [42];
        yield return [long.MaxValue];
        yield return [123.5];
        yield return [79228162514264337593543950335m];
        yield return [Guid.Parse("9b8219ed-9d58-4fe9-a5f4-51a17bd3d75d")];
    }

    public static IEnumerable<object[]> StreamIdentities()
    {
        (string Namespace, string Key)[] identities =
        [
            ("chat", "key"),
            ("energymeter.DataChangedEvent", "key"),
            ("chat", "key.with.dots"),
            ("namespace/slash", "key/slash"),
            ("", "key"),
            ("null", "key"),
            ("~63686174", "~6B6579"),
            ("namespace with whitespace", "key \t\r\n"),
            ("*", ">"),
            (".", "."),
            ("caf\u00e9", "\u03ba\u03bb\u03b5\u03b9\u03b4\u03af"),
            ("chat", "null"),
        ];

        foreach (var (streamNamespace, key) in identities)
        {
            yield return [Encoding.UTF8.GetBytes(streamNamespace), Encoding.UTF8.GetBytes(key)];
        }

        yield return [new byte[] { 0xFF }, new byte[] { 0xFE }];
        yield return [new byte[] { 0xFE }, new byte[] { 0xFF }];
        yield return [Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(), new byte[] { 0, 0xFF, (byte)'/' }];
    }

    [Theory]
    [MemberData(nameof(StreamIdentities))]
    public void StreamIdentityBytesRoundTripThroughNatsStreamSerialization(byte[] streamNamespace, byte[] streamKey)
    {
        var message = new NatsStreamMessage { StreamId = StreamId.Create(streamNamespace, streamKey), Payload = [42] };

        var received = RoundTrip(message);

        Assert.Equal(streamNamespace, received.StreamId.Namespace.ToArray());
        Assert.Equal(streamKey, received.StreamId.Key.ToArray());
        Assert.Equal(message.Payload, received.Payload);
    }

    [Theory]
    [InlineData("\"chat/key\"", "chat", "key")]
    [InlineData("\"energymeter.DataChangedEvent/key\"", "energymeter.DataChangedEvent", "key")]
    [InlineData("\"null/key\"", "null", "key")]
    public void StreamIdentityReadsLegacyJson(string json, string streamNamespace, string streamKey)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Orleans.Streaming.NATS.StreamIdJsonConverter());

        var streamId = JsonSerializer.Deserialize<StreamId>(json, options);

        Assert.Equal(Encoding.UTF8.GetBytes(streamNamespace), streamId.Namespace.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(streamKey), streamId.Key.ToArray());
    }

    [Fact]
    public void StreamIdentityWritesLegacyJsonForTextIdentities()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Orleans.Streaming.NATS.StreamIdJsonConverter());

        var json = JsonSerializer.Serialize(StreamId.Create("chat", "key"), options);

        Assert.Equal("\"chat/key\"", json);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null,\"a2V5\"]")]
    [InlineData("[\"Y2hhdA==\"]")]
    [InlineData("[\"Y2hhdA==\",null]")]
    [InlineData("[\"Y2hhdA==\",\"\"]")]
    [InlineData("[\"Y2hhdA==\",\"a2V5\",\"extra\"]")]
    public void StreamIdentityRejectsMalformedByteComponents(string json)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Orleans.Streaming.NATS.StreamIdJsonConverter());

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StreamId>(json, options));
    }

    [Theory]
    [MemberData(nameof(RequestContextValues))]
    public void RequestContextValuesRoundTripThroughNatsStreamSerialization(object value)
    {
        RequestContext.Clear();

        try
        {
            RequestContext.Set(ContextKey, value);
            var requestContext = RequestContextExtensions.Export(_fixture.DeepCopier);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var message = NatsAdapter.CreateMessage(_fixture.Serializer, streamId, ["event"], requestContext);

            var receivedMessage = RoundTrip(message);
            var receivedBatch = _fixture.Serializer.Deserialize<NatsBatchContainer>(receivedMessage.Payload);

            RequestContext.Clear();

            Assert.NotNull(receivedBatch);
            Assert.True(receivedBatch.ImportRequestContext());
            Assert.Equal(streamId, receivedMessage.StreamId);
            Assert.Equal(streamId, receivedBatch.StreamId);
            Assert.Equal(["event"], receivedBatch.GetEvents<string>().Select(tuple => tuple.Item1));
            Assert.Equal(value, RequestContext.Get(ContextKey));
            Assert.IsType(value.GetType(), RequestContext.Get(ContextKey));
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    private static NatsStreamMessage RoundTrip(NatsStreamMessage message)
    {
        var options = new JsonSerializerOptions();
        options.TypeInfoResolverChain.Add(NatsSerializerContext.Default);
        var registry = new NatsJsonContextOptionsSerializerRegistry(options);
        var buffer = new ArrayBufferWriter<byte>();

        registry.GetSerializer<NatsStreamMessage>().Serialize(buffer, message);

        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        return registry.GetDeserializer<NatsStreamMessage>().Deserialize(in sequence)!;
    }
}
