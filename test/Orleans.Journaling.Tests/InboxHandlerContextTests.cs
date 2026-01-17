using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT"), TestCategory("Journaling")]
public class InboxHandlerContextTests : IClassFixture<DefaultClusterFixture>
{
    private readonly DefaultClusterFixture _fixture;

    public InboxHandlerContextTests(DefaultClusterFixture fixture)
    {
        _fixture = fixture;
    }

    // Helper to create test envelope
    private DurableEnvelope CreateTestEnvelope(GrainId senderId, GrainId receiverId, string routeKey)
    {
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };

        return builder
            .To(receiverId, routeKey)
            .WithBody("test message")
            .Build();
    }

    [Fact]
    public void Constructor_WithValidParameters_SetsProperties()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();

        // Act
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Assert
        Assert.Equal(envelope, context.Envelope);
        Assert.Equal(grainId, context.GrainId);
        Assert.Same(outbox, context.Outbox);
    }

    [Fact]
    public void Envelope_ReturnsCorrectEnvelope()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var result = context.Envelope;

        // Assert
        Assert.Equal(envelope.MessageId, result.MessageId);
        Assert.Equal(envelope.SenderId, result.SenderId);
        Assert.Equal(envelope.ReceiverId, result.ReceiverId);
        Assert.Equal(envelope.RouteKey, result.RouteKey);
    }

    [Fact]
    public void GrainId_ReturnsCorrectGrainId()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var result = context.GrainId;

        // Assert
        Assert.Equal(grainId, result);
    }

    [Fact]
    public void Outbox_ReturnsCorrectOutbox()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var result = context.Outbox;

        // Assert
        Assert.Same(outbox, result);
    }

    [Fact]
    public void CreateEnvelope_ReturnsBuilder()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var builder = context.CreateEnvelope();

        // Assert
        Assert.NotNull(builder);
        Assert.IsType<DurableEnvelopeBuilder>(builder);
    }

    [Fact]
    public void CreateEnvelope_BuilderHasCorrectSenderId()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var builder = context.CreateEnvelope();
        var targetGrain = GrainId.Create("test", "target");
        var newEnvelope = builder.To(targetGrain, "test.route").WithBody("test").Build();

        // Assert
        Assert.Equal(grainId, newEnvelope.SenderId);
    }

    [Fact]
    public void CreateEnvelope_MultipleCalls_ReturnsDifferentBuilders()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var builder1 = context.CreateEnvelope();
        var builder2 = context.CreateEnvelope();

        // Assert
        Assert.NotSame(builder1, builder2);
    }

    [Fact]
    public void Send_WithValidEnvelope_CallsOutboxSend()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        var targetGrain = GrainId.Create("test", "target");
        var outgoingEnvelope = context.CreateEnvelope()
            .To(targetGrain, "target.route")
            .WithBody("outgoing message")
            .Build();

        // Act
        context.Send(outgoingEnvelope);

        // Assert
        Assert.Single(outbox.SentMessages);
        Assert.Equal(outgoingEnvelope.MessageId, outbox.SentMessages[0].MessageId);
    }

    [Fact]
    public void Send_MultipleTimes_CallsOutboxForEach()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        var target1 = GrainId.Create("test", "target1");
        var target2 = GrainId.Create("test", "target2");
        var envelope1 = context.CreateEnvelope().To(target1, "route1").WithBody("msg1").Build();
        var envelope2 = context.CreateEnvelope().To(target2, "route2").WithBody("msg2").Build();

        // Act
        context.Send(envelope1);
        context.Send(envelope2);

        // Assert
        Assert.Equal(2, outbox.SentMessages.Count);
        Assert.Contains(outbox.SentMessages, e => e.ReceiverId == target1);
        Assert.Contains(outbox.SentMessages, e => e.ReceiverId == target2);
    }

    [Fact]
    public void CreateEnvelope_WithReplyPattern_SetsReplyToCorrectly()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act - simulate reply pattern
        var targetGrain = GrainId.Create("test", "target");
        var request = context.CreateEnvelope()
            .To(targetGrain, "work.process")
            .WithBody("work request")
            .WithReplyTo(context.GrainId)  // Reply comes back to this handler's grain
            .Build();

        // Assert
        Assert.Equal(grainId, request.ReplyTo);
    }

    [Fact]
    public void CreateEnvelope_WithCorrelation_PreservesCorrelationKey()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        
        var correlationKey = HierarchicalKey.Create("workflow-123");
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .WithCorrelationKey(correlationKey)
            .Build();

        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act - create child correlation
        var targetGrain = GrainId.Create("test", "target");
        var childKey = context.Envelope.CorrelationKey!.CreateChildKey("step-1");
        var childEnvelope = context.CreateEnvelope()
            .To(targetGrain, "work.process")
            .WithBody("child work")
            .WithCorrelationKey(childKey)
            .Build();

        // Assert
        Assert.NotNull(childEnvelope.CorrelationKey);
        Assert.True(childEnvelope.CorrelationKey!.IsChildOf(correlationKey));
    }

    [Fact]
    public void CreateEnvelope_WithContextValues_SerializesContextCorrectly()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route");
        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act
        var targetGrain = GrainId.Create("test", "target");
        var outgoingEnvelope = context.CreateEnvelope()
            .To(targetGrain, "target.route")
            .WithBody("message")
            .WithContextValue("trace-id", "trace-123")
            .WithContextValue("tenant-id", "tenant-456")
            .Build();

        // Assert
        Assert.True(outgoingEnvelope.Data.HasContextKey("trace-id"));
        Assert.True(outgoingEnvelope.Data.HasContextKey("tenant-id"));
        Assert.True(outgoingEnvelope.Data.TryGetContextValue<string>("trace-id", out var traceId));
        Assert.Equal("trace-123", traceId);
        Assert.True(outgoingEnvelope.Data.TryGetContextValue<string>("tenant-id", out var tenantId));
        Assert.Equal("tenant-456", tenantId);
    }

    [Fact]
    public async Task TypedHandler_WithValidMessage_DeserializesAndInvokes()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();

        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };
        var testMessage = new TestMessage { Value = "test value", Count = 42 };
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody(testMessage)
            .Build();

        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        var handler = new TestMessageHandler();

        // Act - call the interface method explicitly to test default implementation
        await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None);

        // Assert
        Assert.True(handler.WasInvoked);
        Assert.Equal("test value", handler.ReceivedMessage!.Value);
        Assert.Equal(42, handler.ReceivedMessage.Count);
    }

    [Fact]
    public async Task TypedHandler_WithInvalidMessageType_ThrowsInvalidOperationException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();

        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };
        // Send a string instead of TestMessage
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("wrong type")
            .Build();

        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        var handler = new TestMessageHandler();

        // Act & Assert - call the interface method explicitly to test default implementation
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IInboxHandler)handler).HandleAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Handler_AccessingEnvelopeMetadata_CanReadAllFields()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var correlationKey = HierarchicalKey.Create("workflow-456");
        var replyTo = GrainId.Create("test", "reply-target");

        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .WithCorrelationKey(correlationKey)
            .WithReplyTo(replyTo)
            .Build();

        var grainId = GrainId.Create("test", "handler");
        var outbox = new TestOutbox();
        var context = new InboxHandlerContext(envelope, grainId, outbox, sessionPool);

        // Act & Assert
        Assert.Equal(senderId, context.Envelope.SenderId);
        Assert.Equal(receiverId, context.Envelope.ReceiverId);
        Assert.Equal("test.route", context.Envelope.RouteKey);
        Assert.Equal(correlationKey, context.Envelope.CorrelationKey);
        Assert.Equal(replyTo, context.Envelope.ReplyTo);
        Assert.True(context.Envelope.Data.TryGetBody<string>(out var body));
        Assert.Equal("test message", body);
    }

    // Test message type for typed handler tests
    [GenerateSerializer]
    public record TestMessage
    {
        [Id(0)] public required string Value { get; init; }
        [Id(1)] public required int Count { get; init; }
    }

    // Test handler for typed handler tests
    private class TestMessageHandler : IInboxHandler<TestMessage>
    {
        public bool WasInvoked { get; private set; }
        public TestMessage? ReceivedMessage { get; private set; }

        public ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            ReceivedMessage = message;
            return ValueTask.CompletedTask;
        }
    }

    // Simple mock outbox for testing
    private class TestOutbox : IDurableOutbox
    {
        public List<DurableEnvelope> SentMessages { get; } = new();
        private readonly Dictionary<Guid, DurableEnvelope> _messages = new();

        public int Count => _messages.Count;

        public IEnumerable<DurableEnvelope> Messages => _messages.Values;

        public void Send(DurableEnvelope envelope)
        {
            _messages[envelope.MessageId] = envelope;
            SentMessages.Add(envelope);
        }

        public bool RemoveMessage(Guid messageId)
        {
            return _messages.Remove(messageId);
        }

        public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
        {
            return _messages.TryGetValue(messageId, out envelope);
        }

        public Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default)
        {
            // No-op for test purposes
            return Task.CompletedTask;
        }
    }
}
