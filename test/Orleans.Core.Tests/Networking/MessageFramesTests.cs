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
    private readonly Serializer _serializer;

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
        _serializer = _services.GetRequiredService<Serializer>();
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
    public void MessageWhichCannotBeSerialized_FailsTheEnclosingMessage()
    {
        var unserializable = CreateRequest(2, timeToLive: null);
        unserializable.RequestContextData = new() { ["unserializable"] = new UnserializableValue() };

        Assert.ThrowsAny<Exception>(() => _serializer.SerializeToArray(new List<Message> { CreateRequest(1, timeToLive: null), unserializable }));
    }

    private List<Message> RoundTrip(List<Message> messages) => _serializer.Deserialize<List<Message>>(_serializer.SerializeToArray(messages))!;

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
