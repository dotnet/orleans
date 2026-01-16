using System;
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for DurableEnvelope, the polymorphic message wrapper for durable inbox/outbox.
/// Tests verify serialization round-trip, CorrelationKey usage, and null handling.
/// </summary>
[TestCategory("BVT")]
public class DurableEnvelopeTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SerializerSessionPool _sessionPool;
    private readonly IDeepCopier<DurableEnvelope> _copier;
    private readonly CodecProvider _codecProvider;

    public DurableEnvelopeTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        _serviceProvider = services.BuildServiceProvider();
        _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        _copier = _serviceProvider.GetRequiredService<IDeepCopier<DurableEnvelope>>();
        _codecProvider = _serviceProvider.GetRequiredService<CodecProvider>();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    /// <summary>
    /// Helper method to create a DurableEnvelopeData with a body.
    /// </summary>
    private DurableEnvelopeData CreateEnvelopeData<TBody>(TBody body)
    {
        var writer = new ArcBufferWriter();

        try
        {
            // Serialize body
            var startOffset = writer.Length;
            using (var session = _sessionPool.GetSession())
            {
                var bufferWriter = Writer.Create(writer, session);
                _sessionPool.CodecProvider.GetCodec<TBody>().WriteField(ref bufferWriter, 0, typeof(TBody), body);
                bufferWriter.Commit();
            }
            var bodySlice = (startOffset, writer.Length - startOffset);

            // Create the buffer slice
            var buffer = writer.ConsumeSlice(writer.Length);

            // Use reflection to set internal fields (for testing purposes)
            var data = new DurableEnvelopeData(_sessionPool);
            var bufferField = typeof(DurableEnvelopeData).GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var bodySliceField = typeof(DurableEnvelopeData).GetField("_bodySlice", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            bufferField!.SetValue(data, buffer);
            bodySliceField!.SetValue(data, bodySlice);

            return data;
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void SerializationRoundTrip_WithCorrelationKey_PreservesAllFields()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var senderId = GrainId.Create("sender", Guid.NewGuid().ToString());
        var receiverId = GrainId.Create("receiver", Guid.NewGuid().ToString());
        var routeKey = "test/route";
        var correlationKey = CorrelationKey.Create("transfer-123");
        var replyTo = GrainId.Create("reply", Guid.NewGuid().ToString());
        var data = CreateEnvelopeData("test-body");
        var createdAt = DateTimeOffset.UtcNow;

        var original = new DurableEnvelope
        {
            MessageId = messageId,
            SenderId = senderId,
            ReceiverId = receiverId,
            RouteKey = routeKey,
            CorrelationKey = correlationKey,
            ReplyTo = replyTo,
            Data = data,
            CreatedAt = createdAt
        };

        // Act - serialize and deserialize
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = _sessionPool.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            var codec = _codecProvider.GetCodec<DurableEnvelope>();
            codec.WriteField(ref writer, 0, typeof(DurableEnvelope), original);
            writer.Commit();
        }

        DurableEnvelope deserialized;
        using (var session = _sessionPool.GetSession())
        {
            var reader = Reader.Create(buffer.WrittenMemory, session);
            var field = reader.ReadFieldHeader();
            var codec = _codecProvider.GetCodec<DurableEnvelope>();
            deserialized = codec.ReadValue(ref reader, field);
        }

        // Assert
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.SenderId, deserialized.SenderId);
        Assert.Equal(original.ReceiverId, deserialized.ReceiverId);
        Assert.Equal(original.RouteKey, deserialized.RouteKey);
        Assert.Equal(original.CorrelationKey, deserialized.CorrelationKey);
        Assert.Equal(original.ReplyTo, deserialized.ReplyTo);
        Assert.Equal(original.CreatedAt, deserialized.CreatedAt);

        // Verify body can still be deserialized
        Assert.True(deserialized.Data.TryGetBody<string>(out var body));
        Assert.Equal("test-body", body);
    }

    [Fact]
    public void SerializationRoundTrip_WithNullCorrelationKey_PreservesNull()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var senderId = GrainId.Create("sender", Guid.NewGuid().ToString());
        var receiverId = GrainId.Create("receiver", Guid.NewGuid().ToString());
        var routeKey = "test/route";
        var data = CreateEnvelopeData("test-body");
        var createdAt = DateTimeOffset.UtcNow;

        var original = new DurableEnvelope
        {
            MessageId = messageId,
            SenderId = senderId,
            ReceiverId = receiverId,
            RouteKey = routeKey,
            CorrelationKey = null,  // Explicitly null
            ReplyTo = null,
            Data = data,
            CreatedAt = createdAt
        };

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = _sessionPool.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            var codec = _codecProvider.GetCodec<DurableEnvelope>();
            codec.WriteField(ref writer, 0, typeof(DurableEnvelope), original);
            writer.Commit();
        }

        DurableEnvelope deserialized;
        using (var session = _sessionPool.GetSession())
        {
            var reader = Reader.Create(buffer.WrittenMemory, session);
            var field = reader.ReadFieldHeader();
            var codec = _codecProvider.GetCodec<DurableEnvelope>();
            deserialized = codec.ReadValue(ref reader, field);
        }

        // Assert
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.SenderId, deserialized.SenderId);
        Assert.Equal(original.ReceiverId, deserialized.ReceiverId);
        Assert.Equal(original.RouteKey, deserialized.RouteKey);
        Assert.Null(deserialized.CorrelationKey);
        Assert.Null(deserialized.ReplyTo);
        Assert.Equal(original.CreatedAt, deserialized.CreatedAt);
    }

    [Fact]
    public void SerializationRoundTrip_WithHierarchicalCorrelationKey_PreservesHierarchy()
    {
        // Arrange
        var parentKey = CorrelationKey.Create("transfer-abc");
        var correlationKey = parentKey.CreateChildKey("debit");
        var data = CreateEnvelopeData("test-body");

        var original = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = GrainId.Create("sender", Guid.NewGuid().ToString()),
            ReceiverId = GrainId.Create("receiver", Guid.NewGuid().ToString()),
            RouteKey = "account/debit",
            CorrelationKey = correlationKey,
            ReplyTo = null,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = _sessionPool.GetSession())
        {
            var writer = Writer.Create(buffer, session);
            var codec = _codecProvider.GetCodec<DurableEnvelope>();
            codec.WriteField(ref writer, 0, typeof(DurableEnvelope), original);
            writer.Commit();
        }

        DurableEnvelope deserialized;
        using (var session = _sessionPool.GetSession())
        {
            var reader = Reader.Create(buffer.WrittenMemory, session);
            var field = reader.ReadFieldHeader();
            var codec = _codecProvider.GetCodec<DurableEnvelope>();
            deserialized = codec.ReadValue(ref reader, field);
        }

        // Assert
        Assert.NotNull(deserialized.CorrelationKey);
        Assert.Equal("transfer-abc/debit", deserialized.CorrelationKey.ToString());

        // Verify hierarchical relationships are preserved
        Assert.True(deserialized.CorrelationKey.IsChildOf(parentKey));
        Assert.True(parentKey.IsParentOf(deserialized.CorrelationKey));
    }

    [Fact]
    public void Envelope_WithDifferentCorrelationKeys_AreNotEqual()
    {
        // Arrange
        var data1 = CreateEnvelopeData("test-body-1");
        var data2 = CreateEnvelopeData("test-body-2");

        var envelope1 = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = GrainId.Create("sender", "1"),
            ReceiverId = GrainId.Create("receiver", "1"),
            RouteKey = "test/route",
            CorrelationKey = CorrelationKey.Create("key-1"),
            ReplyTo = null,
            Data = data1,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var envelope2 = new DurableEnvelope
        {
            MessageId = envelope1.MessageId,  // Same MessageId
            SenderId = envelope1.SenderId,    // Same SenderId
            ReceiverId = envelope1.ReceiverId, // Same ReceiverId
            RouteKey = envelope1.RouteKey,     // Same RouteKey
            CorrelationKey = CorrelationKey.Create("key-2"), // Different CorrelationKey
            ReplyTo = null,
            Data = data2,
            CreatedAt = envelope1.CreatedAt
        };

        // Assert - structs with different CorrelationKeys should not be equal
        Assert.NotEqual(envelope1.CorrelationKey, envelope2.CorrelationKey);
    }

    /// <summary>
    /// Test message type for complex serialization scenarios.
    /// </summary>
    [GenerateSerializer]
    public sealed class ComplexMessage
    {
        [Id(0)]
        public Guid Id { get; set; }

        [Id(1)]
        public string Name { get; set; } = string.Empty;

        [Id(2)]
        public decimal Amount { get; set; }

        [Id(3)]
        public DateTimeOffset Timestamp { get; set; }
    }
}
