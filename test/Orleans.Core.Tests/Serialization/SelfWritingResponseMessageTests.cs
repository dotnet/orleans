using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace UnitTests.Serialization;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
[TestCategory("BVT"), TestCategory("Serialization")]
public sealed class SelfWritingResponseMessageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_BooleanHolderWithoutCodec_MatchesLegacyFrameAndRoundTrips(bool value)
        => AssertCompatibleFrame(new BooleanResponse(value), value, new BoolCodec());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Write_Int32HolderWithoutCodec_MatchesLegacyFrameAndRoundTrips(int value)
        => AssertCompatibleFrame(new Int32Response(value), value, new Int32Codec());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shared reference payload \u03bb")]
    public void Write_StringHolderWithoutCodec_MatchesLegacyFrameAndRoundTrips(string? value)
        => AssertCompatibleFrame(new StringResponse(value), value!, new StringCodec());

    [Fact]
    public void Write_UnregisteredHolder_InvokesRawWriterWithoutRuntimeCodecLookup()
    {
        // Arrange: querying the holder codec really does fail, independently of message serialization.
        using var environment = new SerializationEnvironment();
        var holder = new Int32Response(314159);
        var message = CreateMessage(holder);
        Assert.Throws<CodecNotFoundException>(() => environment.CodecProvider.GetCodec(holder.GetType()));

        // Act
        var frame = WriteFrame(environment.Serializer, message);

        // Assert
        Assert.Equal(1, holder.WriteCount);
        AssertFrame(frame, message, typeof(int));
        var received = ReadFrame(environment.Serializer, frame);
        using var response = Assert.IsAssignableFrom<Response>(received.BodyObject);
        Assert.Equal(314159, response.GetResult<int>());
        AssertHeaders(message, received);
    }

    [Fact]
    public void Write_RequestImplementingRawWriter_PreservesOrdinaryRequestFrame()
    {
        using var environment = new SerializationEnvironment();
        var body = new RequestWithRawWriter { Value = 47 };
        var message = CreateMessage(Response.Completed);
        message.BodyObject = body;
        message.Direction = Message.Directions.Request;

        var frame = WriteFrame(environment.Serializer, message);
        var received = ReadFrame(environment.Serializer, frame);

        Assert.Equal(0, body.WriteCount);
        Assert.Equal(Message.ResponseTypes.None, message.Result);
        Assert.Equal(Message.Directions.Request, received.Direction);
        Assert.Equal(Message.ResponseTypes.None, received.Result);
        Assert.Equal(47, Assert.IsType<RequestWithRawWriter>(received.BodyObject).Value);
        AssertHeaders(message, received);
    }

    [Fact]
    public void Read_RegisteredReader_IsCachedBeforeLegacyResponseCodecLookup()
    {
        // Arrange: any legacy Response<int> codec resolution is a failing sentinel.
        var reader = new CountingRawReader<int, Int32Codec>(new Int32Codec());
        var counts = new LookupCounts();
        using var sender = new SerializationEnvironment();
        using var receiver = new SerializationEnvironment(options => RegisterReader(options, reader, counts));
        using var firstResponse = Response.FromResult(42);
        using var secondResponse = Response.FromResult(-17);
        var firstMessage = CreateMessage(firstResponse);
        var secondMessage = CreateMessage(secondResponse);
        var firstFrame = WriteFrame(sender.Serializer, firstMessage);
        var secondFrame = WriteFrame(sender.Serializer, secondMessage);

        // Act: legacy-produced frames are accepted using the statically registered reader.
        var first = ReadFrame(receiver.Serializer, firstFrame);
        var second = ReadFrame(receiver.Serializer, secondFrame);

        // Assert
        using var firstResult = Assert.IsType<Response<int>>(first.BodyObject);
        using var secondResult = Assert.IsType<Response<int>>(second.BodyObject);
        Assert.Equal(42, firstResult.TypedResult);
        Assert.Equal(-17, secondResult.TypedResult);
        Assert.Equal(1, counts.ReaderFactoryCalls);
        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(0, counts.LegacyCodecLookups);
        AssertHeaders(firstMessage, first);
        AssertHeaders(secondMessage, second);
    }

    [Theory]
    [InlineData((int)Message.ResponseTypes.Success)]
    [InlineData((int)Message.ResponseTypes.Error)]
    [InlineData((int)Message.ResponseTypes.Rejection)]
    [InlineData((int)Message.ResponseTypes.Status)]
    public void Write_NonNoneResponseType_DoesNotInvokeRawWriter(int responseTypeValue)
    {
        // Arrange
        using var environment = new SerializationEnvironment();
        var responseType = (Message.ResponseTypes)responseTypeValue;
        var holder = new Int32Response(42);
        var message = CreateMessage(holder);
        message.Result = responseType;
        using var buffer = new ArcBufferWriter();
        Assert.Throws<CodecNotFoundException>(() => environment.Serializer.Write(buffer, message));
        Assert.Equal(0, holder.WriteCount);
        Assert.Equal(responseType, message.Result);
    }

    [Fact]
    public void Write_OversizedRawBody_EnforcesLimitAndSerializerCanBeReused()
    {
        // Arrange
        using var environment = new SerializationEnvironment(
            messagingOptions: new SiloMessagingOptions { MaxMessageBodySize = 32 });
        var oversized = new StringResponse(new string('x', 128));
        using (var buffer = new ArcBufferWriter())
        {
            // Act
            var exception = Assert.Throws<InvalidMessageFrameException>(
                () => environment.Serializer.Write(buffer, CreateMessage(oversized)));

            // Assert
            Assert.Contains("Invalid body size:", exception.Message);
            Assert.Contains(nameof(MessagingOptions.MaxMessageBodySize), exception.Message);
            Assert.Equal(1, oversized.WriteCount);
        }

        AssertSuccessfulReuse(environment);
    }

    [Fact]
    public void Write_RawWriterThrows_PropagatesExceptionAndSerializerCanBeReused()
    {
        // Arrange
        using var environment = new SerializationEnvironment();
        var holder = new ThrowingResponse();
        using (var buffer = new ArcBufferWriter())
        {
            // Act
            var exception = Assert.Throws<InvalidOperationException>(
                () => environment.Serializer.Write(buffer, CreateMessage(holder)));

            // Assert
            Assert.Same(holder.Failure, exception);
            Assert.Equal(1, holder.WriteCount);
        }

        AssertSuccessfulReuse(environment);
    }

    private static void AssertCompatibleFrame<TResult, TCodec>(TestResponse holder, TResult value, TCodec codec)
        where TCodec : class, IFieldCodec<TResult>
    {
        // Arrange: isolated providers, no holder codec, and a poisoned legacy reader fallback.
        var rawReader = new CountingRawReader<TResult, TCodec>(codec);
        var counts = new LookupCounts();
        using var sender = new SerializationEnvironment();
        using var receiver = new SerializationEnvironment(options => RegisterReader(options, rawReader, counts));
        using var legacyResponse = Response.FromResult(value);
        var message = CreateMessage(holder);
        if (value is string referenceValue)
        {
            // The body also appears in the header, exercising the header/body partial session reset.
            message.RequestContextData!["payload"] = referenceValue;
        }

        Assert.Throws<CodecNotFoundException>(() => sender.CodecProvider.GetCodec(holder.GetType()));

        // Act: use the same stable headers, explicitly resetting the raw response flag for each write.
        message.Result = Message.ResponseTypes.None;
        var directFrame = WriteFrame(sender.Serializer, message);
        message.BodyObject = legacyResponse;
        message.Result = Message.ResponseTypes.None;
        var legacyFrame = WriteFrame(sender.Serializer, message);
        var received = ReadFrame(receiver.Serializer, directFrame);

        // Assert: compare the entire frame, not just the serialized payload.
        Assert.Equal(legacyFrame.HeaderLength, directFrame.HeaderLength);
        Assert.Equal(legacyFrame.BodyLength, directFrame.BodyLength);
        Assert.Equal(legacyFrame.Bytes, directFrame.Bytes);
        Assert.Equal(1, holder.WriteCount);
        AssertFrame(directFrame, message, typeof(TResult));
        using var response = Assert.IsType<Response<TResult>>(received.BodyObject);
        Assert.Equal(value, response.TypedResult);
        Assert.Null(response.Exception);
        Assert.Equal(1, counts.ReaderFactoryCalls);
        Assert.Equal(1, rawReader.ReadCount);
        Assert.Equal(0, counts.LegacyCodecLookups);
        AssertHeaders(message, received);
    }

    private static void RegisterReader<TResult, TCodec>(
        TypeManifestOptions options, CountingRawReader<TResult, TCodec> reader, LookupCounts counts)
        where TCodec : class, IFieldCodec<TResult>
    {
        options.AddRawResponseReader<TResult>(_ =>
        {
            counts.ReaderFactoryCalls++;
            return reader;
        });
        options.AddSerializer<Response<TResult>>(_ =>
        {
            counts.LegacyCodecLookups++;
            throw new InvalidOperationException("The registered raw reader must precede legacy Response<T> codec lookup.");
        }, _ => new ShallowCopier<Response<TResult>>());
    }

    private static Message CreateMessage(Response body)
    {
        var shared = new byte[] { 3, 1, 4 };
        return new Message
        {
            BodyObject = body,
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.None,
            Id = new CorrelationId(123456),
            SendingGrain = GrainId.Create("sender", "fixed"),
            TargetGrain = GrainId.Create("target", "fixed"),
            SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 7),
            TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 22222), 9),
            IsReadOnly = true,
            IsAlwaysInterleave = true,
            InterfaceVersion = 2,
            ForwardCount = 1,
            RequestContextData = new Dictionary<string, object>
            {
                ["first"] = shared,
                ["second"] = shared,
                ["number"] = 73
            }
        };
    }

    internal static (byte[] Bytes, int HeaderLength, int BodyLength) WriteFrame(MessageSerializer serializer, Message message)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[Message.LENGTH_HEADER_SIZE]);
        var (headerLength, bodyLength) = serializer.Write(buffer, message);
        Span<byte> lengths = stackalloc byte[Message.LENGTH_HEADER_SIZE];
        BinaryPrimitives.WriteInt32LittleEndian(lengths, headerLength);
        BinaryPrimitives.WriteInt32LittleEndian(lengths[sizeof(int)..], bodyLength);
        buffer.WriteAt(0, lengths);
        using var frame = buffer.ConsumeSlice(buffer.Length);
        return (frame.ToArray(), headerLength, bodyLength);
    }

    internal static Message ReadFrame(MessageSerializer serializer, (byte[] Bytes, int HeaderLength, int BodyLength) frame)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(frame.Bytes.AsSpan(Message.LENGTH_HEADER_SIZE));
        using var shared = new MessageHandlerShared(null!, null!, () => serializer, null!, null!, null!);
        using var request = shared.GetReceiveMessageHandler();
        request.Headers = buffer.ConsumeSlice(frame.HeaderLength);
        request.Body = buffer.ConsumeSlice(frame.BodyLength);
        serializer.ReadHeaders(request, out var message);
        serializer.ReadBodyObject(message, request);
        Assert.Equal(0, buffer.Length);
        return message;
    }

    private static void AssertFrame(
        (byte[] Bytes, int HeaderLength, int BodyLength) frame, Message message, Type resultType)
    {
        Assert.Equal(frame.HeaderLength, BinaryPrimitives.ReadInt32LittleEndian(frame.Bytes));
        Assert.Equal(frame.BodyLength, BinaryPrimitives.ReadInt32LittleEndian(frame.Bytes.AsSpan(4)));
        Assert.Equal(Message.LENGTH_HEADER_SIZE + frame.HeaderLength + frame.BodyLength, frame.Bytes.Length);
        Assert.True(frame.BodyLength > 0);
        var expectedHeaders = message.Headers;
        expectedHeaders.ResponseType = Message.ResponseTypes.Success;
        Assert.Equal((uint)expectedHeaders, BinaryPrimitives.ReadUInt32LittleEndian(frame.Bytes.AsSpan(Message.LENGTH_HEADER_SIZE)));

        using var environment = new SerializationEnvironment();
        using var session = environment.Services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(frame.Bytes.AsSpan(Message.LENGTH_HEADER_SIZE + frame.HeaderLength), session);
        var field = reader.ReadFieldHeader();
        Assert.Equal(resultType, field.FieldType);
        Assert.Equal(WireType.TagDelimited, field.WireType);
    }

    private static void AssertHeaders(Message source, Message received)
    {
        // The transport-only Success flag must not escape into the reconstructed message.
        var expected = source.Headers;
        expected.ResponseType = Message.ResponseTypes.None;
        Assert.Equal((uint)expected, (uint)received.Headers);
        Assert.Equal(source.Id, received.Id);
        Assert.Equal(source.SendingGrain, received.SendingGrain);
        Assert.Equal(source.TargetGrain, received.TargetGrain);
        Assert.Equal(source.SendingSilo, received.SendingSilo);
        Assert.Equal(source.TargetSilo, received.TargetSilo);
        Assert.Equal(source.InterfaceVersion, received.InterfaceVersion);
        var context = Assert.IsType<Dictionary<string, object>>(received.RequestContextData);
        Assert.Equal(source.RequestContextData!.Count, context.Count);
        Assert.Equal(new byte[] { 3, 1, 4 }, Assert.IsType<byte[]>(context["first"]));
        Assert.Same(context["first"], context["second"]);
        Assert.Equal(73, Assert.IsType<int>(context["number"]));
        if (source.RequestContextData.TryGetValue("payload", out var payload))
        {
            Assert.Equal(payload, context["payload"]);
        }
    }

    private static void AssertSuccessfulReuse(SerializationEnvironment environment)
    {
        var holder = new Int32Response(42);
        var message = CreateMessage(holder);
        var frame = WriteFrame(environment.Serializer, message);
        var received = ReadFrame(environment.Serializer, frame);
        using var response = Assert.IsAssignableFrom<Response>(received.BodyObject);
        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(1, holder.WriteCount);
        AssertHeaders(message, received);
    }

    private sealed class SerializationEnvironment : IDisposable
    {
        public SerializationEnvironment(Action<TypeManifestOptions>? configure = null, MessagingOptions? messagingOptions = null)
        {
            var services = new ServiceCollection();
            // Raw reader registration is first-wins: install the test reader before assembly manifests.
            if (configure is not null) services.Configure(configure);
            services.AddSerializer(builder =>
            {
                // Include both header codec dependencies, without starting a client or silo.
                builder.AddAssembly(typeof(MessageSerializer).Assembly);
                builder.AddAssembly(typeof(GrainAddressCacheUpdate).Assembly);
            });
            Services = services.BuildServiceProvider();
            CodecProvider = Services.GetRequiredService<CodecProvider>();
            Serializer = new MessageSerializer(
                Services.GetRequiredService<SerializerSessionPool>(), messagingOptions ?? new SiloMessagingOptions());
        }

        public ServiceProvider Services { get; }
        public CodecProvider CodecProvider { get; }
        public MessageSerializer Serializer { get; }

        public void Dispose()
        {
            Serializer.Dispose();
            Services.Dispose();
        }
    }

    private sealed class LookupCounts
    {
        public int ReaderFactoryCalls;
        public int LegacyCodecLookups;
    }

    [GenerateSerializer]
    public sealed class RequestWithRawWriter : IRawResponseWriter
    {
        [Id(0)]
        public int Value { get; set; }

        [NonSerialized]
        public int WriteCount;

        public void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
        {
            WriteCount++;
            throw new InvalidOperationException("A request body uses its ordinary field codec.");
        }
    }

    private sealed class CountingRawReader<TResult, TCodec>(TCodec codec) : IRawResponseReader
        where TCodec : class, IFieldCodec<TResult>
    {
        private readonly PooledResponseCodec<TResult, TCodec> _codec = new(codec);
        public int ReadCount { get; private set; }
        public bool IsSupported => true;

        public Response ReadRaw<TInput>(ref Reader<TInput> reader, scoped ref Field field)
        {
            ReadCount++;
            Assert.Equal(typeof(TResult), field.FieldType);
            return (Response)_codec.ReadRaw(ref reader, ref field);
        }
    }

    // Deliberately concrete, non-generic holders with no GenerateSerializer or registered field codec.
    private abstract class TestResponse : Response, IRawResponseWriter
    {
        public int WriteCount { get; protected set; }
        public override object? Result { get; set; }
        public override Exception? Exception { get => null; set => throw new NotSupportedException(); }
        public override T GetResult<T>() => (T)Result!;
        public override void Dispose() { }
        public abstract void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>;
    }

    private sealed class BooleanResponse : TestResponse
    {
        public BooleanResponse(bool value) => Result = value;

        public override void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer)
        {
            WriteCount++;
            writer.WriteStartObject(0, null!, typeof(bool));
            BoolCodec.WriteField(ref writer, 0, (bool)Result!);
            writer.WriteEndObject();
        }
    }

    private sealed class Int32Response : TestResponse
    {
        public Int32Response(int value) => Result = value;

        public override void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer)
        {
            WriteCount++;
            writer.WriteStartObject(0, null!, typeof(int));
            Int32Codec.WriteField(ref writer, 0, (int)Result!);
            writer.WriteEndObject();
        }
    }

    private sealed class StringResponse : TestResponse
    {
        public StringResponse(string? value) => Result = value;

        public override void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer)
        {
            WriteCount++;
            writer.WriteStartObject(0, null!, typeof(string));
            if (Result is string value) StringCodec.WriteField(ref writer, 0, value);
            writer.WriteEndObject();
        }
    }

    private sealed class ThrowingResponse : TestResponse
    {
        public InvalidOperationException Failure { get; } = new("Raw writer failure");

        public override void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer)
        {
            WriteCount++;
            writer.WriteStartObject(0, null!, typeof(int));
            Int32Codec.WriteField(ref writer, 0, 99);
            throw Failure;
        }
    }
}
