using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT"), TestCategory("Journaling")]
public class DurableEnvelopeBuilderTests : IClassFixture<DefaultClusterFixture>
{
    private readonly DefaultClusterFixture _fixture;

    public DurableEnvelopeBuilderTests(DefaultClusterFixture fixture)
    {
        _fixture = fixture;
    }

    // Helper to create a builder with required dependencies
    private DurableEnvelopeBuilder CreateBuilder(GrainId senderId)
    {
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        return new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };
    }

    [Fact]
    public void To_WithValidParameters_SetsReceiverAndRoute()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var result = builder.To(receiverId, "test.route");

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void To_WithNullRouteKey_ThrowsArgumentNullException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => builder.To(receiverId, null!));
    }

    [Fact]
    public void To_WithEmptyRouteKey_ThrowsArgumentException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.To(receiverId, string.Empty));
    }

    [Fact]
    public void To_WithWhitespaceRouteKey_ThrowsArgumentException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.To(receiverId, "   "));
    }

    [Fact]
    public void WithBody_WithValidBody_SerializesBody()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var result = builder
            .To(receiverId, "test.route")
            .WithBody("test message");

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void WithBody_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        builder.To(receiverId, "test.route").WithBody("first");

        // Assert
        Assert.Throws<InvalidOperationException>(() => builder.WithBody("second"));
    }

    [Fact]
    public void WithCorrelationKey_WithCorrelationKeyObject_SetsCorrelation()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);
        var correlationKey = HierarchicalKey.Create("transfer-123");

        // Act
        var result = builder
            .To(receiverId, "test.route")
            .WithBody("test")
            .WithCorrelationKey(correlationKey);

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void WithCorrelationKey_WithString_SetsCorrelation()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var result = builder
            .To(receiverId, "test.route")
            .WithBody("test")
            .WithCorrelationKey("transfer-123/debit");

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void WithCorrelationKey_WithNullString_ThrowsArgumentNullException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => builder.WithCorrelationKey((string)null!));
    }

    [Fact]
    public void WithReplyTo_WithValidGrainId_SetsReplyTo()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var replyTo = GrainId.Create("test", "reply-receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var result = builder
            .To(receiverId, "test.route")
            .WithBody("test")
            .WithReplyTo(replyTo);

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void WithContextValue_WithValidKeyAndValue_AddsContext()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var result = builder
            .To(receiverId, "test.route")
            .WithBody("test")
            .WithContextValue("trace-id", "abc-123");

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void WithContextValue_WithMultipleKeys_AddsAllContext()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var result = builder
            .To(receiverId, "test.route")
            .WithBody("test")
            .WithContextValue("trace-id", "abc-123")
            .WithContextValue("tenant-id", "tenant-456")
            .WithContextValue("user-id", 789);

        // Assert
        Assert.Same(builder, result); // Fluent API returns same instance
    }

    [Fact]
    public void WithContextValue_WithNullKey_ThrowsArgumentNullException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => builder.WithContextValue<string>(null!, "value"));
    }

    [Fact]
    public void WithContextValue_WithEmptyKey_ThrowsArgumentException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.WithContextValue(string.Empty, "value"));
    }

    [Fact]
    public void WithContextValue_WithWhitespaceKey_ThrowsArgumentException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var builder = CreateBuilder(senderId);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.WithContextValue("   ", "value"));
    }

    [Fact]
    public void WithContextValue_WithDuplicateKey_ThrowsInvalidOperationException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        builder
            .To(receiverId, "test.route")
            .WithBody("test")
            .WithContextValue("trace-id", "abc-123");

        // Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.WithContextValue("trace-id", "def-456"));
        Assert.Contains("trace-id", ex.Message);
    }

    [Fact]
    public void Build_WithAllRequiredFields_CreatesEnvelope()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .Build();

        // Assert
        Assert.NotEqual(Guid.Empty, envelope.MessageId);
        Assert.Equal(senderId, envelope.SenderId);
        Assert.Equal(receiverId, envelope.ReceiverId);
        Assert.Equal("test.route", envelope.RouteKey);
        Assert.True(envelope.CreatedAt <= DateTimeOffset.UtcNow);
        Assert.True(envelope.CreatedAt >= DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public void Build_WithoutBody_ThrowsInvalidOperationException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        builder.To(receiverId, "test.route");

        // Assert
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("body", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Build_WithoutTo_ThrowsInvalidOperationException()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var builder = CreateBuilder(senderId);

        // Act
        builder.WithBody("test message");

        // Assert
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("route", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Build_WithOptionalFields_IncludesAllFields()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var replyTo = GrainId.Create("test", "reply");
        var correlationKey = HierarchicalKey.Create("transfer-123/debit");
        var builder = CreateBuilder(senderId);

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .WithCorrelationKey(correlationKey)
            .WithReplyTo(replyTo)
            .WithContextValue("trace-id", "abc-123")
            .Build();

        // Assert
        Assert.Equal(correlationKey, envelope.CorrelationKey);
        Assert.Equal(replyTo, envelope.ReplyTo);
        Assert.True(envelope.Data.HasContextKey("trace-id"));
    }

    [Fact]
    public void Build_OrderIndependence_BodyBeforeContext_Works()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .WithContextValue("trace-id", "abc-123")
            .Build();

        // Assert
        Assert.True(envelope.Data.TryGetBody<string>(out var body));
        Assert.Equal("test message", body);
        Assert.True(envelope.Data.TryGetContextValue<string>("trace-id", out var traceId));
        Assert.Equal("abc-123", traceId);
    }

    [Fact]
    public void Build_OrderIndependence_ContextBeforeBody_Works()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithContextValue("trace-id", "abc-123")
            .WithBody("test message")
            .Build();

        // Assert
        Assert.True(envelope.Data.TryGetBody<string>(out var body));
        Assert.Equal("test message", body);
        Assert.True(envelope.Data.TryGetContextValue<string>("trace-id", out var traceId));
        Assert.Equal("abc-123", traceId);
    }

    [Fact]
    public void Build_WithMultipleContextValues_AllAccessible()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .WithContextValue("trace-id", "abc-123")
            .WithContextValue("tenant-id", "tenant-456")
            .WithContextValue("user-id", 789)
            .Build();

        // Assert
        Assert.True(envelope.Data.TryGetContextValue<string>("trace-id", out var traceId));
        Assert.Equal("abc-123", traceId);
        Assert.True(envelope.Data.TryGetContextValue<string>("tenant-id", out var tenantId));
        Assert.Equal("tenant-456", tenantId);
        Assert.True(envelope.Data.TryGetContextValue<int>("user-id", out var userId));
        Assert.Equal(789, userId);
    }

    [Fact]
    public void Reset_ClearsAllFields()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var replyTo = GrainId.Create("test", "reply");
        var builder = CreateBuilder(senderId);

        // Act - build first envelope
        var envelope1 = builder
            .To(receiverId, "test.route")
            .WithBody("first message")
            .WithCorrelationKey("correlation-1")
            .WithReplyTo(replyTo)
            .WithContextValue("trace-id", "abc-123")
            .Build();

        // Reset and build second envelope
        builder.Reset();
        var envelope2 = builder
            .To(receiverId, "test.route2")
            .WithBody("second message")
            .Build();

        // Assert
        Assert.NotEqual(envelope1.MessageId, envelope2.MessageId);
        Assert.Null(envelope2.CorrelationKey);
        Assert.Null(envelope2.ReplyTo);
        Assert.False(envelope2.Data.HasContextKey("trace-id"));
    }

    [Fact]
    public void Build_WithComplexBody_PreservesType()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);
        var request = new TestRequest { Id = 123, Name = "test", Amount = 45.67m };

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody(request)
            .Build();

        // Assert
        Assert.True(envelope.Data.TryGetBody<TestRequest>(out var body));
        Assert.Equal(123, body.Id);
        Assert.Equal("test", body.Name);
        Assert.Equal(45.67m, body.Amount);
    }

    [Fact]
    public void Build_WithHierarchicalCorrelationKey_PreservesHierarchy()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var builder = CreateBuilder(senderId);
        var parentKey = HierarchicalKey.Create("transfer-123");
        var childKey = parentKey.CreateChildKey("debit");

        // Act
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .WithCorrelationKey(childKey)
            .Build();

        // Assert
        Assert.Equal(childKey, envelope.CorrelationKey);
        Assert.True(childKey.IsChildOf(parentKey));
    }
}

[GenerateSerializer, Immutable]
public sealed record TestRequest
{
    [Id(0)] public int Id { get; init; }
    [Id(1)] public string Name { get; init; } = string.Empty;
    [Id(2)] public decimal Amount { get; init; }
}
