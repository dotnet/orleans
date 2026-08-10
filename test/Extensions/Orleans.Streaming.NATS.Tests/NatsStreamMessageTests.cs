using System.Buffers;
using System.Text.Json;
using NATS.Client.Serializers.Json;
using Orleans.Runtime;
using Orleans.Streaming.NATS;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

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
