using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using TestExtensions;
using Xunit;

namespace Orleans.Core.Tests.Networking;

/// <summary>
/// Verifies that messages serialized within other messages, as requests travel with a migrating activation, read back as received messages.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public sealed class MessageFramesTests : IDisposable
{
    private readonly ServiceProvider _services;

    public MessageFramesTests()
    {
        _services = new ServiceCollection()
            .AddSerializer()
            .AddMetrics()
            .AddSingleton(services =>
            {
                var instruments = new OrleansInstruments(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
                var messaging = new MessagingInstruments(instruments);
                var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, new MessagingProcessingInstruments(instruments));
                var sessions = services.GetRequiredService<SerializerSessionPool>();
                return new MessageHandlerShared(
                    trace,
                    new ConnectionTrace(NullLoggerFactory.Instance),
                    () => new MessageSerializer(sessions, new SiloMessagingOptions()),
                    new MessageFactory(services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace),
                    Substitute.For<IMessageCenter>(),
                    messaging);
            })
            .BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public void Messages_ReadBackInOrderWithTheirHeaders_AndTheirBodiesSerialized()
    {
        var first = CreateRequest(1, TimeSpan.FromMinutes(1));
        first.BodyObject = new List<string> { "first", "second" };
        first.RequestContextData = new() { ["key"] = "value" };
        var second = CreateRequest(2, timeToLive: null);
        second.BodyObject = 42;

        var messages = RoundTrip([first, second]);
        try
        {
            Assert.Equal([first.Id, second.Id], messages.Select(message => message.Id));
            var firstCopy = messages[0];
            Assert.Equal(first.Direction, firstCopy.Direction);
            Assert.Equal(first.SendingGrain, firstCopy.SendingGrain);
            Assert.Equal(first.TargetGrain, firstCopy.TargetGrain);
            Assert.Equal("value", firstCopy.RequestContextData!["key"]);
            Assert.InRange(firstCopy.TimeToLive!.Value, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
            Assert.Null(messages[1].TimeToLive);

            // Each body stays serialized, as a received message's body does, until it is accessed.
            Assert.IsType<MessageReadRequest>(firstCopy._bodyObject);
            Assert.Equal(["first", "second"], Assert.IsType<List<string>>(firstCopy.BodyObject));
            Assert.Equal(42, messages[1].BodyObject);
        }
        finally
        {
            DisposeAll(messages);
        }
    }

    [Fact]
    public void MessageWithPooledValues_IsDeliveredByItsReadBackCopy()
    {
        var expected = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        var request = CreateRequest(1, timeToLive: null);
        request.BodyObject = CreatePooledBuffer(expected);

        // Serializing the message returns its pooled buffer's storage to the pool, as sending it does, so the message which is delivered is the copy which is read back.
        var copy = Assert.Single(RoundTrip([request]));
        try
        {
            Assert.Equal(expected, ((PooledBuffer)copy.BodyObject!).ToArray());
        }
        finally
        {
            copy.Dispose();
        }
    }

    [Fact]
    public void ReadBackCopy_IsSerializedAgainWithoutDecodingItsBody()
    {
        var request = CreateRequest(1, timeToLive: null);
        request.BodyObject = new List<string> { "value" };
        var copy = Assert.Single(RoundTrip([request]));
        try
        {
            // Serializing the copy again, as forwarding or migrating it again does, sends its serialized body unchanged.
            var again = Assert.Single(RoundTrip([copy]));
            try
            {
                Assert.IsType<MessageReadRequest>(copy._bodyObject);
                Assert.IsType<MessageReadRequest>(again._bodyObject);
                Assert.Equal(["value"], Assert.IsType<List<string>>(again.BodyObject));
            }
            finally
            {
                again.Dispose();
            }
        }
        finally
        {
            copy.Dispose();
        }
    }

    [Fact]
    public void MessageWhichCannotBeSerialized_FailsTheSnapshot()
    {
        var unserializable = CreateRequest(2, timeToLive: null);
        unserializable.RequestContextData = new() { ["unserializable"] = new UnserializableValue() };

        Assert.ThrowsAny<Exception>(() => MessageFrames.WriteRequests(_services.GetRequiredService<MessageHandlerShared>(), [CreateRequest(1, timeToLive: null), unserializable]));
    }

    [Fact]
    public void SnapshotSurvivesSourceDisposalAndCanBeReadTwice()
    {
        var request = CreateRequest(1, timeToLive: null);
        request.BodyObject = "value";

        var shared = _services.GetRequiredService<MessageHandlerShared>();
        var snapshot = MessageFrames.WriteRequests(shared, [request]);
        request.Dispose();
        for (var i = 0; i < 2; i++)
        {
            using var copy = Assert.Single(MessageFrames.ReadRequests(shared, snapshot));
            Assert.Equal(request.Id, copy.Id);
            Assert.Equal("value", copy.BodyObject);
        }
    }

    [Fact]
    public void MalformedSnapshot_ReleasesDecodedPrefix()
    {
        var shared = _services.GetRequiredService<MessageHandlerShared>();
        using var request = CreateRequest(1, timeToLive: null);
        request.BodyObject = new byte[20_000];
        var snapshot = MessageFrames.WriteRequests(shared, [request]).Append((byte)0).ToArray();
        var handler = shared.GetReceiveMessageHandler();
        handler.Reset();

        Assert.Throws<InvalidMessageFrameException>(() => MessageFrames.ReadRequests(shared, snapshot));
        Assert.Equal(0, handler.BodyLength);
        var returned = shared.GetReceiveMessageHandler();
        Assert.Same(handler, returned);
        returned.Reset();
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(10, 20_000)]
    public void SnapshotMessages_SharePagesAndKeepBodiesSerialized(int count, int payloadSize)
    {
        var shared = _services.GetRequiredService<MessageHandlerShared>();
        var requests = Enumerable.Range(0, count).Select(i =>
        {
            var message = CreateRequest(i, timeToLive: null);
            message.BodyObject = Enumerable.Repeat((byte)i, payloadSize).ToArray();
            return message;
        }).ToList();
        var snapshot = MessageFrames.WriteRequests(shared, requests);
        var received = MessageFrames.ReadRequests(shared, snapshot);
        var pages = new HashSet<byte[]>();

        try
        {
            Assert.Equal(count, received.Count);
            for (var i = 0; i < count; i++)
            {
                var message = received[i];
                var body = Assert.IsType<MessageReadRequest>(message._bodyObject);
                foreach (var segment in body.Body.ArraySegments)
                {
                    pages.Add(segment.Array!);
                }

                Assert.Equal(new CorrelationId(i), message.Id);
                Assert.Equal(Enumerable.Repeat((byte)i, payloadSize), Assert.IsType<byte[]>(message.BodyObject));
            }

            Assert.InRange(pages.Count, 1, (snapshot.Length + ArcBufferWriter.MinimumPageSize - 1) / ArcBufferWriter.MinimumPageSize);
        }
        finally
        {
            DisposeAll(received);
        }
    }

    private List<Message> RoundTrip(List<Message> messages)
    {
        var shared = _services.GetRequiredService<MessageHandlerShared>();
        return MessageFrames.ReadRequests(shared, MessageFrames.WriteRequests(shared, messages));
    }

    [Fact]
    public void PreparedRequestBody_SurvivesNonConsumingContextSizingAndForwarding()
    {
        var shared = _services.GetRequiredService<MessageHandlerShared>();
        var serializer = shared.GetMessageSerializer();
        var payload = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        var contextPayload = Enumerable.Repeat((byte)0x2B, 30_000).ToArray();
        using var message = CreateRequest(1, timeToLive: null);
        message.BodyObject = CreatePooledBuffer(payload);
        var contextBuffer = CreatePooledBuffer(contextPayload);
        message.RequestContextData = new() { ["pooled"] = contextBuffer };
        try
        {
            using var scratch = new ArcBufferWriter();
            var length = serializer.PrepareRequestBody(shared, scratch, message);
            var body = Assert.IsType<MessageReadRequest>(message._bodyObject);
            Assert.Equal(body.BodyLength, length);
            Assert.Equal(length, serializer.PrepareRequestBody(shared, scratch, message));
            Assert.Same(body, message._bodyObject);
            Assert.Equal(new SiloMessagingOptions().MaxMessageHeaderSize, serializer.GetHeaderSizeBound(scratch, message));
            Assert.Equal(contextPayload, contextBuffer.ToArray());

            using var frame = new ArcBufferWriter();
            MessageFrames.Write(serializer, frame, message);
            using var received = MessageFrames.Read(shared, frame);
            using var receivedBody = Assert.IsType<PooledBuffer>(received.BodyObject);
            using var receivedContext = Assert.IsType<PooledBuffer>(received.RequestContextData!["pooled"]);
            Assert.Equal(payload, receivedBody.ToArray());
            Assert.Equal(contextPayload, receivedContext.ToArray());
        }
        finally
        {
            shared.Return(serializer);
        }
    }

    [Fact]
    public void HeaderSizeBound_UsesCurrentCacheUpdatesAndAllowsAddressGrowth()
    {
        var shared = _services.GetRequiredService<MessageHandlerShared>();
        var serializer = shared.GetMessageSerializer();
        using var message = Assert.Single(RoundTrip([CreateRequest(1, timeToLive: null)]));
        var updateGrain = GrainId.Create("test", new string('k', 4096));
        message.AddToCacheInvalidationHeader(new GrainAddress { GrainId = updateGrain }, validAddress: null);
        try
        {
            using var buffer = new ArcBufferWriter();
            var bound = serializer.GetHeaderSizeBound(buffer, message);
            Assert.True(bound > 4096);
            message.SendingSilo = SiloAddress.New(System.Net.IPAddress.IPv6Loopback, 65535, int.MaxValue);
            message.TargetSilo = SiloAddress.New(System.Net.IPAddress.IPv6Loopback, 65534, int.MinValue);
            var (headerLength, _) = MessageFrames.Write(serializer, buffer, message);
            Assert.InRange((long)headerLength, 4097, bound);
        }
        finally
        {
            shared.Return(serializer);
        }
    }

    private static Message CreateRequest(long id, TimeSpan? timeToLive) => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        TargetGrain = GrainId.Create("test", "target"),
        TimeToLive = timeToLive,
    };

    private static PooledBuffer CreatePooledBuffer(byte[] contents)
    {
        var buffer = new PooledBuffer();
        buffer.Write(contents);
        return buffer;
    }

    private static void DisposeAll(List<Message> messages)
    {
        foreach (var message in messages)
        {
            message.Dispose();
        }
    }

    private sealed class UnserializableValue;
}
