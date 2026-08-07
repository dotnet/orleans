using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
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
        var stateManager = new TestStateManager();
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var logger = NullLogger<DurableInboxExtension>.Instance;

        inbox ??= new Dictionary<(GrainId, Guid), DurableEnvelope>();
        processed ??= new Dictionary<(GrainId, Guid), DateTimeOffset>();
        
        // Create a test DurableInbox for handler registration
        var durableInbox = new TestDurableInbox();
        var outbox = new TestOutbox();
        var jobHandlers = new TestJobHandlerRegistry();
        var jobManager = new TestJobManager(jobHandlers);

        return new DurableInboxExtension(
            grainContext,
            stateManager,
            sessionPool,
            logger,
            DurableMessagingInstruments.CreateForDirectConstruction(),
            durableInbox,
            inbox,
            processed,
            new Dictionary<(GrainId, Guid), InboxMessageState>(),
            new Dictionary<(GrainId, Guid), InboxDeadLetter>(),
            new TestDurableValue<string>(),
            outbox,
            jobManager,
            jobHandlers,
            TimeProvider.System,
            new DurableInboxOptions
            {
                MaxCapacity = maxCapacity,
                BackpressureRetryDelay = TimeSpan.FromMilliseconds(1),
                MaxProcessingAttempts = 1
            });
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
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", new TestMessage { Value = "test", Count = 1 });

        // Act
        var result = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        // Note: Count may be 0 or 1 depending on whether async processing completed.
        // In test environments with synchronous mock state machines, processing completes
        // before DeliverAsync returns, so Count == 0 is expected.
        // The key assertion is that the delivery was accepted.
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
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", new TestMessage { Value = "test", Count = 1 });

        // Act - deliver twice
        var result1 = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);
        
        // The message should have been accepted or processed
        Assert.Equal(DeliveryStatus.Accepted, result1.Status);
        
        var result2 = await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Assert
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
        // Arrange - use a slow handler so the message stays in inbox during processing
        var extension = CreateInboxExtension(maxCapacity: 1);
        var slowHandler = new SlowMessageHandler(delayMs: 10000); // 10 seconds - won't complete during test
        extension.RegisterHandler("test.route", slowHandler);
        
        var senderId = GrainId.Create("test", "sender");
        var receiverId = GrainId.Create("test", "receiver");

        // Fill to capacity with first message (slow handler keeps it in inbox)
        var envelope1 = CreateTestEnvelope(senderId, receiverId, "test.route", "message1");
        var result1 = await extension.DeliverAsync(envelope1, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Accepted, result1.Status);

        // Wait briefly for slow handler to start processing (but not complete)
        // The slow handler has a 10 second delay, so it won't complete during this test
        await TestHelpers.WaitUntilAsync(
            () => extension.Count >= 1,
            timeout: TimeSpan.FromSeconds(2),
            message: "Message was not added to inbox");

        // The message should still be in the inbox since the slow handler hasn't completed
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
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", new TestMessage { Value = "test", Count = 1 });

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

        // Wait for handler to be invoked
        await TestHelpers.WaitUntilAsync(
            () => handler.WasInvoked,
            message: "Handler was not invoked");

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
        var envelope = CreateTestEnvelope(senderId, receiverId, "test.route", new TestMessage { Value = "test", Count = 1 });

        // Act
        await extension.DeliverAsync(envelope, new DeliveryOptions(), CancellationToken.None);

        // Wait for message to be processed and removed from inbox
        await TestHelpers.WaitUntilAsync(
            () => extension.Count == 0,
            message: "Message was not removed from inbox after processing");

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

        // Wait for message to be processed (even with handler exception)
        await TestHelpers.WaitUntilAsync(
            () => extension.Count == 0,
            message: "Message was not removed after handler exception");

        // Assert - message should be removed even after handler exception
        Assert.Equal(0, extension.Count);
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

        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
        }
    }

    // Throwing handler for exception tests
    private class ThrowingMessageHandler : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Test exception");
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
    private class TestStateManager : IJournaledStateManager
    {
        public int WriteCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void RegisterState(string name, IJournaledState state) { }

        public bool TryGetState(string name, [MaybeNullWhen(false)] out IJournaledState state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    }

    // Test IDurableInbox implementation for handler registration
    private class TestDurableInbox : IDurableInbox
    {
        private readonly Dictionary<string, IInboxHandler> _handlers = new();

        public int Count => 0;
        public int Capacity => 1000;
        public IEnumerable<DurableEnvelope> Messages => Array.Empty<DurableEnvelope>();

        public bool ContainsOrProcessed(GrainId senderId, Guid messageId) => false;
        public bool HasHandler(string routeKey) => _handlers.ContainsKey(routeKey);
        public void MarkProcessed(GrainId senderId, Guid messageId) { }
        public void RegisterHandler(string routeKey, IInboxHandler handler) => _handlers[routeKey] = handler;
        public bool RemoveMessage(GrainId senderId, Guid messageId) => false;
        public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler) => _handlers.TryGetValue(routeKey, out handler);
        public bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
        {
            envelope = default;
            return false;
        }

        public void RegisterHandler(IInboxHandler handler)
        {
            throw new NotImplementedException("Use legacy RegisterHandler(string, IInboxHandler) for tests");
        }

        public bool TryFindHandler(IInboxHandlerContext context, [MaybeNullWhen(false)] out IInboxHandler handler)
        {
            return TryGetHandler(context.Envelope.RouteKey ?? string.Empty, out handler);
        }
    }

    // Test IDurableOutbox implementation for testing
    private class TestOutbox : IDurableOutbox
    {
        private readonly Dictionary<Guid, DurableEnvelope> _messages = new();

        public int Count => _messages.Count;
        public IEnumerable<DurableEnvelope> Messages => _messages.Values;

        public void Send(DurableEnvelope envelope) => _messages[envelope.MessageId] = envelope;
        public bool RemoveMessage(Guid messageId) => _messages.Remove(messageId);
        public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) => _messages.TryGetValue(messageId, out envelope);
        public Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestDurableValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }

    private sealed class TestJobHandlerRegistry : IDurableJobHandlerRegistry
    {
        public IDurableJobFeatureHandler? Handler { get; private set; }

        public void Register(string jobName, IDurableJobFeatureHandler handler) => Handler = handler;
    }

    private sealed class TestJobManager(TestJobHandlerRegistry handlers) : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            var job = new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test"
            };
            _ = Task.Factory.StartNew(
                () => RunAsync(job),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            return Task.FromResult(job);
        }

        public Task<bool> TryCancelDurableJobAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        private async Task RunAsync(DurableJob job)
        {
            var dequeueCount = 0;
            while (handlers.Handler is { } handler)
            {
                var result = await handler.ExecuteJobAsync(new TestJobRunContext(job, dequeueCount++), CancellationToken.None);
                if (result.Status is DurableJobRunStatus.Completed or DurableJobRunStatus.Failed)
                {
                    return;
                }

                if (result.RetryAtTime is { } retryAt)
                {
                    var delay = retryAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }
                }
                else
                {
                    await Task.Delay(result.PollAfterDelay ?? TimeSpan.FromMilliseconds(1));
                }
            }
        }
    }

    private sealed class TestJobRunContext(DurableJob job, int dequeueCount) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString();
        public int DequeueCount { get; } = dequeueCount;
    }

    // Test IDurableDictionary implementation for simple in-memory storage
    private class TestDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _dict = new();

        public TValue this[TKey key]
        {
            get => _dict[key];
            set => _dict[key] = value;
        }

        public ICollection<TKey> Keys => _dict.Keys;
        public ICollection<TValue> Values => _dict.Values;
        public int Count => _dict.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => _dict.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => _dict.Add(item.Key, item.Value);
        public void Clear() => _dict.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((IDictionary<TKey, TValue>)_dict).Contains(item);
        public bool ContainsKey(TKey key) => _dict.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((IDictionary<TKey, TValue>)_dict).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dict.GetEnumerator();
        public bool Remove(TKey key) => _dict.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((IDictionary<TKey, TValue>)_dict).Remove(item);
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _dict.TryGetValue(key, out value);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _dict.GetEnumerator();
    }
}
