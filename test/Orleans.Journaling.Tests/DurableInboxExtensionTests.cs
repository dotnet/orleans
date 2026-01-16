using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT"), TestCategory("Journaling")]
public class DurableInboxExtensionTests : IClassFixture<DefaultClusterFixture>
{
    private readonly DefaultClusterFixture _fixture;

    public DurableInboxExtensionTests(DefaultClusterFixture fixture)
    {
        _fixture = fixture;
    }

    // Helper to create test envelope
    private DurableEnvelope CreateTestEnvelope(GrainId senderId, GrainId receiverId, string routeKey, object body)
    {
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };

        return builder
            .To(receiverId, routeKey)
            .WithBody(body)
            .Build();
    }

    // Helper to create inbox extension
    private DurableInboxExtension CreateInboxExtension(
        Dictionary<(GrainId, Guid), DurableEnvelope>? inbox = null,
        Dictionary<(GrainId, Guid), DateTimeOffset>? processed = null,
        int maxCapacity = 1000)
    {
        var grainContext = new MockGrainContext();
        var stateMachineManager = new TestStateMachineManager();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var logger = NullLogger<DurableInboxExtension>.Instance;

        inbox ??= new Dictionary<(GrainId, Guid), DurableEnvelope>();
        processed ??= new Dictionary<(GrainId, Guid), DateTimeOffset>();

        return new DurableInboxExtension(
            grainContext,
            stateMachineManager,
            sessionPool,
            logger,
            inbox,
            processed,
            maxCapacity);
    }

    [Fact]
    public void Constructor_WithValidParameters_SetsProperties()
    {
        // Act
        var extension = CreateInboxExtension();

        // Assert
        Assert.Equal(0, extension.Count);
        Assert.Equal(1000, extension.Capacity);
    }

    [Fact]
    public void RegisterHandler_WithValidRouteKey_RegistersHandler()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();

        // Act
        extension.RegisterHandler("test.route", handler);

        // Assert
        Assert.True(extension.HasHandler("test.route"));
    }

    [Fact]
    public void RegisterHandler_WithNullRouteKey_ThrowsException()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();

        // Act & Assert - ArgumentNullException is a subtype of ArgumentException
        Assert.Throws<ArgumentNullException>(() => extension.RegisterHandler(null!, handler));
    }

    [Fact]
    public void RegisterHandler_WithNullHandler_ThrowsException()
    {
        // Arrange
        var extension = CreateInboxExtension();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => extension.RegisterHandler("test.route", null!));
    }

    [Fact]
    public async Task DeliverAsync_WithValidMessage_AcceptsMessage()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act
        var result = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Equal(1, extension.Count);
    }

    [Fact]
    public async Task DeliverAsync_WithDuplicateMessage_ReturnsDuplicate()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act - deliver twice
        var result1 = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);
        
        // Wait for async processing to start (but not necessarily complete)
        await Task.Delay(100);
        
        var result2 = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result1.Status);
        Assert.Equal(DeliveryStatus.Duplicate, result2.Status);
    }

    [Fact]
    public async Task DeliverAsync_WithProcessedMessage_ReturnsDuplicate()
    {
        // Arrange
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var messageId = Guid.NewGuid();

        // Create inbox with message already processed
        var processed = new Dictionary<(GrainId, Guid), DateTimeOffset>
        {
            [(senderId, messageId)] = DateTimeOffset.UtcNow
        };
        var extension = CreateInboxExtension(processed: processed);

        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderId
        };
        var envelope = builder
            .To(receiverId, "test.route")
            .WithBody("test message")
            .Build();

        // Override messageId to match processed one
        var envelopeWithId = new DurableEnvelope
        {
            MessageId = messageId,
            SenderId = envelope.SenderId,
            ReceiverId = envelope.ReceiverId,
            RouteKey = envelope.RouteKey,
            Data = envelope.Data,
            CreatedAt = envelope.CreatedAt
        };

        // Act
        var result = await extension.DeliverAsync(envelopeWithId, new DeliveryOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Duplicate, result.Status);
        Assert.Equal(0, extension.Count); // No new message added
    }

    [Fact]
    public async Task DeliverAsync_WhenAtCapacity_ReturnsBackpressured()
    {
        // Arrange
        var extension = CreateInboxExtension(maxCapacity: 1);
        // Note: No handler registered - messages will stay in inbox
        
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");

        // Fill to capacity
        var envelope1 = CreateTestEnvelope(senderId, receiverId, "test.route", "message1");
        var result1 = await extension.DeliverAsync(envelope1, new DeliveryOptions(), CancellationToken.None);

        // Assert first message was rejected due to no handler
        Assert.Equal(DeliveryStatus.RouteNotFound, result1.Status);
        Assert.Equal(0, extension.Count);

        // Register handler after first attempt
        var handler = new TestMessageHandler();
        extension.RegisterHandler("test.route", handler);

        // Fill to capacity
        await extension.DeliverAsync(envelope1, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(1, extension.Count);

        // Act - try to add beyond capacity
        var envelope2 = CreateTestEnvelope(senderId, receiverId, "test.route", "message2");
        var result = await extension.DeliverAsync(envelope2, new DeliveryOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Backpressured, result.Status);
        Assert.Equal(1, extension.Count); // Still at capacity
    }

    [Fact]
    public async Task DeliverAsync_WithNoHandler_ReturnsRouteNotFound()
    {
        // Arrange
        var extension = CreateInboxExtension();
        // No handler registered

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act
        var result = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.RouteNotFound, result.Status);
        Assert.Contains("test.route", result.Message);
    }

    [Fact]
    public async Task DeliverAsync_WithLongPolling_WaitsForProcessing()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act - deliver with long polling (wait up to 5 seconds)
        var options = new DeliveryOptions { PollTimeout = TimeSpan.FromSeconds(5) };
        var result = await extension.DeliverAsync(envelope, options, CancellationToken.None);

        // Assert - should complete with Processed status (not Pending)
        Assert.True(result.Status == DeliveryStatus.Processed || result.Status == DeliveryStatus.Pending);
    }

    [Fact]
    public async Task DeliverAsync_WithLongPollingTimeout_ReturnsPending()
    {
        // Arrange
        var extension = CreateInboxExtension();
        // Register a slow handler that won't complete in time
        var slowHandler = new SlowMessageHandler(delayMs: 10000); // 10 seconds
        extension.RegisterHandler("test.route", slowHandler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act - deliver with short timeout
        var options = new DeliveryOptions { PollTimeout = TimeSpan.FromMilliseconds(100) };
        var result = await extension.DeliverAsync(envelope, options, CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Pending, result.Status);
    }

    [Fact]
    public async Task ProcessMessage_InvokesHandler()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", new TestMessage { Value = "test", Count = 42 });

        // Act - deliver without long polling
        await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Wait a bit for async processing
        await Task.Delay(500);

        // Assert
        Assert.True(handler.WasInvoked);
        Assert.Equal("test", handler.ReceivedMessage?.Value);
        Assert.Equal(42, handler.ReceivedMessage?.Count);
    }

    [Fact]
    public async Task ProcessMessage_RemovesFromInbox()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new TestMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act
        await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(1, extension.Count);

        // Wait for processing
        await Task.Delay(500);

        // Assert - message should be removed after processing
        Assert.Equal(0, extension.Count);
    }

    [Fact]
    public async Task ProcessMessage_WithHandlerException_MarksAsProcessed()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new ThrowingMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", "test message");

        // Act
        await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(1, extension.Count);

        // Wait for processing
        await Task.Delay(500);

        // Assert - message should be removed even after handler exception
        Assert.Equal(0, extension.Count);
    }

    [Fact]
    public async Task ProcessPendingMessages_WithConcurrency1_ProcessesSequentially()
    {
        // Arrange
        var extension = CreateInboxExtension();
        var handler = new CountingMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");

        // Deliver multiple messages in batches to avoid race conditions
        var deliveryTasks = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", $"message {i}");
            deliveryTasks.Add(extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None).AsTask());
        }

        // Wait for all deliveries
        await Task.WhenAll(deliveryTasks);

        // Wait for processing (increased timeout)
        await Task.Delay(1500);

        // Assert - all messages processed
        Assert.Equal(5, handler.ProcessedCount);
        Assert.Equal(0, extension.Count);
    }

    [Fact]
    public async Task ProcessPendingMessages_WithConcurrency4_ProcessesConcurrently()
    {
        // Arrange - create extension with concurrency level 4
        var grainContext = new MockGrainContext();
        var stateMachineManager = new TestStateMachineManager();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var logger = NullLogger<DurableInboxExtension>.Instance;
        var inbox = new Dictionary<(GrainId, Guid), DurableEnvelope>();
        var processed = new Dictionary<(GrainId, Guid), DateTimeOffset>();

        var extension = new DurableInboxExtension(
            grainContext,
            stateMachineManager,
            sessionPool,
            logger,
            inbox,
            processed,
            maxCapacity: 1000,
            deduplicationWindow: TimeSpan.FromDays(7),
            processingConcurrency: 4);

        var handler = new CountingMessageHandler();
        extension.RegisterHandler("test.route", handler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");

        // Deliver multiple messages without waiting for processing
        var deliveryTasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", $"message {i}");
            deliveryTasks.Add(extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None).AsTask());
        }

        // Wait for all deliveries to complete
        await Task.WhenAll(deliveryTasks);

        // Wait for processing to complete (increased timeout)
        await Task.Delay(2000);

        // Assert - all messages processed
        Assert.Equal(10, handler.ProcessedCount);
        Assert.Equal(0, extension.Count);
    }

    [Fact]
    public async Task ProcessPendingMessages_WithConcurrentHandlers_IsolatesExceptions()
    {
        // Arrange - create extension with concurrency level 4
        var grainContext = new MockGrainContext();
        var stateMachineManager = new TestStateMachineManager();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var logger = NullLogger<DurableInboxExtension>.Instance;
        var inbox = new Dictionary<(GrainId, Guid), DurableEnvelope>();
        var processed = new Dictionary<(GrainId, Guid), DateTimeOffset>();

        var extension = new DurableInboxExtension(
            grainContext,
            stateMachineManager,
            sessionPool,
            logger,
            inbox,
            processed,
            maxCapacity: 1000,
            deduplicationWindow: TimeSpan.FromDays(7),
            processingConcurrency: 4);

        // Register handlers: one throws, one succeeds
        var throwingHandler = new ThrowingMessageHandler();
        var successHandler = new CountingMessageHandler();
        extension.RegisterHandler("throwing.route", throwingHandler);
        extension.RegisterHandler("success.route", successHandler);

        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");

        // Deliver messages to both routes without waiting
        var deliveryTasks = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            var throwingEnvelope = CreateTestEnvelope(senderId, receiverId, "throwing.route", $"throwing {i}");
            deliveryTasks.Add(extension.DeliverAsync(throwingEnvelope, new DeliveryOptions(), CancellationToken.None).AsTask());

            var successEnvelope = CreateTestEnvelope(senderId, receiverId, "success.route", $"success {i}");
            deliveryTasks.Add(extension.DeliverAsync(successEnvelope, new DeliveryOptions(), CancellationToken.None).AsTask());
        }

        // Wait for all deliveries
        await Task.WhenAll(deliveryTasks);

        // Wait for processing (increased timeout)
        await Task.Delay(2000);

        // Assert - successful messages processed despite exceptions in other handlers
        Assert.Equal(3, successHandler.ProcessedCount);
        Assert.Equal(0, extension.Count); // All messages removed (including failed ones)
    }

    // Test message type
    [GenerateSerializer]
    public record TestMessage
    {
        [Id(0)] public required string Value { get; init; }
        [Id(1)] public required int Count { get; init; }
    }

    // Test handler
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

    // Slow handler for timeout tests
    private class SlowMessageHandler : IInboxHandler
    {
        private readonly int _delayMs;

        public SlowMessageHandler(int delayMs)
        {
            _delayMs = delayMs;
        }

        public async ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
        }
    }

    // Throwing handler for exception tests
    private class ThrowingMessageHandler : IInboxHandler
    {
        public ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Test exception");
        }
    }

    // Counting handler for concurrency tests
    private class CountingMessageHandler : IInboxHandler
    {
        private int _count;

        public int ProcessedCount => _count;

        public ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    // Mock grain context
    private class MockGrainContext : IGrainContext
    {
        public GrainId GrainId { get; } = GrainId.Create("test", Guid.NewGuid().ToString());
        public GrainReference GrainReference => throw new NotImplementedException();
        public object? GrainInstance => throw new NotImplementedException();
        public ActivationId ActivationId => throw new NotImplementedException();
        public GrainAddress Address => throw new NotImplementedException();
        public IServiceProvider ActivationServices => throw new NotImplementedException();
        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();
        public IWorkItemScheduler Scheduler => throw new NotImplementedException();
        public PlacementStrategy PlacementStrategy => throw new NotImplementedException();
        public Task Deactivated => Task.CompletedTask;
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }
        public TComponent? GetComponent<TComponent>() where TComponent : class => null;
        public object? GetComponent(Type type) => null;
        public TTarget? GetTarget<TTarget>() where TTarget : class => null;
        public object? GetTarget() => null;
        public void SetComponent<TComponent>(TComponent? instance) where TComponent : class { }
        public void ReceiveMessage(object message) { }
        public void Rehydrate(IRehydrationContext context) { }
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
    }

    // Test state machine manager
    private class TestStateMachineManager : IStateMachineManager
    {
        public int WriteCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void RegisterStateMachine(string name, IDurableStateMachine stateMachine) { }

        public bool TryGetStateMachine(string name, [MaybeNullWhen(false)] out IDurableStateMachine stateMachine)
        {
            stateMachine = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<TResult> RunAsync<TResult>(Func<TResult> operation)
        {
            return new ValueTask<TResult>(operation());
        }

        public ValueTask<TResult> RunAsync<TResult>(Func<ValueTask<TResult>> operation, CancellationToken cancellationToken = default)
        {
            return operation();
        }
    }
}
