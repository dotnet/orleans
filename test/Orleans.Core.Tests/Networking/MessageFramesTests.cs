using System;
using System.Buffers.Binary;
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
/// Verifies that messages written as frames read back as received messages, which is how requests travel with a migrating activation.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public sealed class MessageFramesTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly MessageHandlerShared _shared;

    public MessageFramesTests()
    {
        _services = new ServiceCollection().AddSerializer().AddMetrics().BuildServiceProvider();
        var instruments = new OrleansInstruments(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var messaging = new MessagingInstruments(instruments);
        var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, new MessagingProcessingInstruments(instruments));
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        _shared = new MessageHandlerShared(
            trace,
            new ConnectionTrace(NullLoggerFactory.Instance),
            () => new MessageSerializer(sessions, new SiloMessagingOptions()),
            new MessageFactory(_services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace),
            Substitute.For<IMessageCenter>(),
            messaging);
    }

    public void Dispose()
    {
        _shared.Dispose();
        _services.Dispose();
    }

    [Fact]
    public void Messages_ReadBackInOrderWithTheirHeaders_AndTheirBodiesSerialized()
    {
        var first = CreateRequest(1, TimeSpan.FromMinutes(1));
        first.BodyObject = new List<string> { "first", "second" };
        first.RequestContextData = new() { ["key"] = "value" };
        var second = CreateRequest(2, timeToLive: null);
        second.BodyObject = 42;

        using var buffer = new ArcBufferWriter();
        var written = MessageFrames.Write(_shared, buffer, first);
        written += MessageFrames.Write(_shared, buffer, second);
        Assert.Equal(buffer.Length, written);

        var messages = MessageFrames.Read(_shared, ToArray(buffer));
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
    public void WrittenMessageWithPooledValues_IsDeliveredByItsReadBackCopy()
    {
        var expected = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        var request = CreateRequest(1, timeToLive: null);
        request.BodyObject = CreatePooledBuffer(expected);

        // Writing the message returns its pooled buffer's storage to the pool, so the message which is delivered afterwards is the copy read back from its frame.
        using var buffer = new ArcBufferWriter();
        MessageFrames.Write(_shared, buffer, request);

        var copy = Assert.Single(MessageFrames.Read(_shared, ToArray(buffer)));
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
    public void ReadBackCopy_IsWrittenAgainWithoutDecodingItsBody()
    {
        // The body stands in for one carrying a type which this process cannot decode.
        var request = CreateRequest(1, timeToLive: null);
        request.BodyObject = new List<string> { "value" };
        using var buffer = new ArcBufferWriter();
        MessageFrames.Write(_shared, buffer, request);
        var frames = ToArray(buffer);
        frames[^1] ^= 0xFF;

        var copy = Assert.Single(MessageFrames.Read(_shared, frames));
        try
        {
            // Writing the copy, as forwarding it does, sends its serialized body unchanged.
            using var forwarded = new ArcBufferWriter();
            MessageFrames.Write(_shared, forwarded, copy);
            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(frames.AsSpan(sizeof(int)));
            Assert.Equal(frames[^bodyLength..], ToArray(forwarded)[^bodyLength..]);
            Assert.IsType<MessageReadRequest>(copy._bodyObject);
        }
        finally
        {
            copy.Dispose();
        }
    }

    [Fact]
    public void MessageWhichCannotBeWritten_LeavesTheBufferUnchanged()
    {
        var written = CreateRequest(1, timeToLive: null);
        var unwritable = CreateRequest(2, timeToLive: null);
        unwritable.RequestContextData = new() { ["unserializable"] = new UnserializableValue() };

        using var buffer = new ArcBufferWriter();
        MessageFrames.Write(_shared, buffer, written);
        var length = buffer.Length;
        Assert.ThrowsAny<Exception>(() => MessageFrames.Write(_shared, buffer, unwritable));
        Assert.Equal(length, buffer.Length);

        var copy = Assert.Single(MessageFrames.Read(_shared, ToArray(buffer)));
        Assert.Equal(written.Id, copy.Id);
        copy.Dispose();
    }

    [Fact]
    public void IncompleteFrame_IsRejected()
    {
        using var buffer = new ArcBufferWriter();
        MessageFrames.Write(_shared, buffer, CreateRequest(1, timeToLive: null));
        MessageFrames.Write(_shared, buffer, CreateRequest(2, timeToLive: null));
        var frames = ToArray(buffer);

        Assert.Throws<InvalidMessageFrameException>(() => MessageFrames.Read(_shared, frames.AsSpan(0, frames.Length - 1)));
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

    private static byte[] ToArray(ArcBufferWriter buffer)
    {
        var result = new byte[buffer.Length];
        buffer.Peek(result);
        return result;
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
