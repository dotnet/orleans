using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for InboxProcessingPump with DurableJobs integration.
/// </summary>
[TestCategory("BVT"), TestCategory("Journaling")]
public class InboxProcessingPumpTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly SerializerSessionPool _sessionPool;

    public InboxProcessingPumpTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
    }
    private sealed class TestDurableJobContext : IDurableJobContext
    {
        public DurableJob Job { get; }
        public string RunId { get; }
        public int DequeueCount { get; }

        public TestDurableJobContext(DurableJob job, string runId, int dequeueCount)
        {
            Job = job;
            RunId = runId;
            DequeueCount = dequeueCount;
        }
    }

    private static GrainId CreateTestGrainId(string type = "test", string key = null!)
    {
        return GrainId.Create(type, key ?? Guid.NewGuid().ToString());
    }

    private static DurableEnvelope CreateTestEnvelope(
        GrainId? senderId = null,
        GrainId? receiverId = null,
        Guid? messageId = null,
        string routeKey = "test-route")
    {
        return new DurableEnvelope
        {
            MessageId = messageId ?? Guid.NewGuid(),
            SenderId = senderId ?? CreateTestGrainId("sender"),
            ReceiverId = receiverId ?? CreateTestGrainId("receiver"),
            RouteKey = routeKey,
            Data = new DurableEnvelopeData(null!),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Mock implementation of IDurableDictionary for testing.
    /// </summary>
    private sealed class MockDurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _inner = new();

        public TValue this[TKey key] { get => _inner[key]; set => _inner[key] = value; }
        public ICollection<TKey> Keys => _inner.Keys;
        public ICollection<TValue> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value) => _inner.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Add(item);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Contains(item);
        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
        public bool Remove(TKey key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Remove(item);
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _inner.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();
    }

    /// <summary>
    /// Test implementation of IDurableInbox.
    /// </summary>
    private sealed class TestInbox : IDurableInbox
    {
        private readonly MockDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _messages = new();
        private readonly MockDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _internalProcessed = new();
        private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset>? _externalProcessed;
        private readonly Dictionary<string, IInboxHandler> _handlers = new();
        private readonly int _capacity;

        public TestInbox(int capacity = 1000, IDictionary<(GrainId, Guid), DateTimeOffset>? processed = null)
        {
            _capacity = capacity;
            _externalProcessed = processed;
        }

        public int Count => _messages.Count;
        public int Capacity => _capacity;
        public IEnumerable<DurableEnvelope> Messages => _messages.Values;

        public bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
        {
            return _messages.TryGetValue((senderId, messageId), out envelope);
        }

        public bool RemoveMessage(GrainId senderId, Guid messageId)
        {
            return _messages.Remove((senderId, messageId));
        }

        public bool ContainsOrProcessed(GrainId senderId, Guid messageId)
        {
            var key = (senderId, messageId);
            return _messages.ContainsKey(key) 
                || _internalProcessed.ContainsKey(key) 
                || (_externalProcessed?.ContainsKey(key) == true);
        }

        public void MarkProcessed(GrainId senderId, Guid messageId)
        {
            _internalProcessed[(senderId, messageId)] = DateTimeOffset.UtcNow;
        }

        public void RegisterHandler(string routeKey, IInboxHandler handler)
        {
            _handlers[routeKey] = handler;
        }

        public bool HasHandler(string routeKey)
        {
            return _handlers.ContainsKey(routeKey);
        }

        public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler)
        {
            return _handlers.TryGetValue(routeKey, out handler);
        }

        // Test helpers
        public void AddMessage(DurableEnvelope envelope)
        {
            _messages[(envelope.SenderId, envelope.MessageId)] = envelope;
        }

        public bool IsProcessed(GrainId senderId, Guid messageId)
        {
            var key = (senderId, messageId);
            return _internalProcessed.ContainsKey(key) || (_externalProcessed?.ContainsKey(key) == true);
        }
    }

    /// <summary>
    /// Test implementation of IDurableOutbox.
    /// </summary>
    private sealed class TestOutbox : IDurableOutbox
    {
        private readonly List<DurableEnvelope> _messages = new();

        public int Count => _messages.Count;
        public IEnumerable<DurableEnvelope> Messages => _messages;

        public void Send(DurableEnvelope envelope)
        {
            _messages.Add(envelope);
        }

        public bool RemoveMessage(Guid messageId)
        {
            var index = _messages.FindIndex(e => e.MessageId == messageId);
            if (index >= 0)
            {
                _messages.RemoveAt(index);
                return true;
            }
            return false;
        }

        public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
        {
            envelope = _messages.FirstOrDefault(e => e.MessageId == messageId);
            return envelope.MessageId != Guid.Empty;
        }
    }

    /// <summary>
    /// Test handler that tracks invocations.
    /// </summary>
    private sealed class TestHandler : IInboxHandler
    {
        private readonly Func<DurableEnvelope, IInboxHandlerContext, CancellationToken, ValueTask>? _action;
        public int InvocationCount { get; private set; }
        public DurableEnvelope? LastEnvelope { get; private set; }

        public TestHandler(Func<DurableEnvelope, IInboxHandlerContext, CancellationToken, ValueTask>? action = null)
        {
            _action = action;
        }

        public async ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            LastEnvelope = envelope;

            if (_action is not null)
            {
                await _action(envelope, context, cancellationToken);
            }
        }
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenJobManagerIsNull()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InboxProcessingPump(
            null!,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenInboxIsNull()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InboxProcessingPump(
            jobManager,
            null!,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenOutboxIsNull()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InboxProcessingPump(
            jobManager,
            inbox,
            null!,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenProcessedIsNull()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            null!,
            stateMachineManager,
            sessionPool,
            logger,
            grainId));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenSessionPoolIsNull()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            null!,
            logger,
            grainId));
    }

    [Fact]
    public async Task SchedulePumpAsync_ReturnsNull_WhenInboxIsEmpty()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Act
        var job = await pump.SchedulePumpAsync();

        // Assert
        Assert.Null(job);
        await jobManager.DidNotReceiveWithAnyArgs().ScheduleJobAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task SchedulePumpAsync_SchedulesJob_WhenInboxHasMessages()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Add a message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId);
        inbox.AddMessage(envelope);

        var expectedJob = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };

        jobManager.ScheduleJobAsync(grainId, "inbox-processing-pump", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>())
            .Returns(expectedJob);

        // Act
        var job = await pump.SchedulePumpAsync();

        // Assert
        Assert.NotNull(job);
        Assert.Equal(expectedJob, job);
        await jobManager.Received(1).ScheduleJobAsync(grainId, "inbox-processing-pump", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_ProcessesMessage_WhenHandlerExists()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var inbox = new TestInbox(processed: processed);
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Register handler
        var handler = new TestHandler();
        inbox.RegisterHandler("test-route", handler);

        // Add message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(envelope.MessageId, handler.LastEnvelope?.MessageId);
        Assert.Equal(0, inbox.Count); // Message removed from inbox
        Assert.True(inbox.IsProcessed(envelope.SenderId, envelope.MessageId)); // Message marked as processed
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_RemovesMessage_WhenNoHandlerRegistered()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var inbox = new TestInbox(processed: processed);
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Add message to inbox (no handler registered)
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "unknown-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(0, inbox.Count); // Message removed from inbox
        Assert.True(inbox.IsProcessed(envelope.SenderId, envelope.MessageId)); // Message marked as processed
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_HandlerCanSendMessages_ViaContext()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Register handler that sends a message
        var replyEnvelope = CreateTestEnvelope(senderId: grainId, routeKey: "reply-route");
        var handler = new TestHandler(async (env, ctx, ct) =>
        {
            ctx.Send(replyEnvelope);
            await ValueTask.CompletedTask;
        });
        inbox.RegisterHandler("test-route", handler);

        // Add message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(0, inbox.Count); // Message removed from inbox
        Assert.Equal(1, outbox.Count); // Reply sent to outbox
        Assert.Equal(replyEnvelope.MessageId, outbox.Messages.First().MessageId);
    }

    [Fact]
    public async Task ExecuteJobAsync_RemovesMessage_WhenHandlerThrowsException_AndRemoveOnExceptionIsTrue()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var inbox = new TestInbox(processed: processed);
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId,
            removeOnHandlerException: true);

        // Register handler that throws
        var handler = new TestHandler((env, ctx, ct) => throw new InvalidOperationException("Test exception"));
        inbox.RegisterHandler("test-route", handler);

        // Add message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(0, inbox.Count); // Message removed from inbox
        Assert.True(inbox.IsProcessed(envelope.SenderId, envelope.MessageId)); // Message marked as processed
    }

    [Fact]
    public async Task ExecuteJobAsync_KeepsMessage_WhenHandlerThrowsException_AndRemoveOnExceptionIsFalse()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId,
            removeOnHandlerException: false);

        // Register handler that throws
        var handler = new TestHandler((env, ctx, ct) => throw new InvalidOperationException("Test exception"));
        inbox.RegisterHandler("test-route", handler);

        // Add message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(1, inbox.Count); // Message still in inbox
        Assert.False(inbox.IsProcessed(envelope.SenderId, envelope.MessageId)); // Message not marked as processed
    }

    [Fact]
    public async Task ExecuteJobAsync_ProcessesMultipleMessages_InOrder()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var inbox = new TestInbox(processed: processed);
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Register handler
        var handler = new TestHandler();
        inbox.RegisterHandler("test-route", handler);

        // Add multiple messages to inbox
        var envelope1 = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        var envelope2 = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        var envelope3 = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope1);
        inbox.AddMessage(envelope2);
        inbox.AddMessage(envelope3);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(3, handler.InvocationCount);
        Assert.Equal(0, inbox.Count); // All messages removed from inbox
        Assert.True(inbox.IsProcessed(envelope1.SenderId, envelope1.MessageId));
        Assert.True(inbox.IsProcessed(envelope2.SenderId, envelope2.MessageId));
        Assert.True(inbox.IsProcessed(envelope3.SenderId, envelope3.MessageId));
    }

    [Fact]
    public async Task ExecuteJobAsync_ReschedulesPump_WhenMessagesRemainAfterProcessing()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId,
            removeOnHandlerException: false); // Keep failed messages

        // Register handler that throws
        var handler = new TestHandler((env, ctx, ct) => throw new InvalidOperationException("Test exception"));
        inbox.RegisterHandler("test-route", handler);

        // Add message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(1, inbox.Count); // Message still in inbox
        await jobManager.Received(1).ScheduleJobAsync(grainId, "inbox-processing-pump", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_DoesNotReschedule_WhenInboxIsEmpty()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var inbox = new TestInbox();
        var outbox = new TestOutbox();
        var processed = new MockDurableDictionary<(GrainId, Guid), DateTimeOffset>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var sessionPool = _sessionPool;
        var logger = NullLogger<InboxProcessingPump>.Instance;
        var grainId = CreateTestGrainId();

        var pump = new InboxProcessingPump(
            jobManager,
            inbox,
            outbox,
            processed,
            stateMachineManager,
            sessionPool,
            logger,
            grainId);

        // Register handler
        var handler = new TestHandler();
        inbox.RegisterHandler("test-route", handler);

        // Add message to inbox
        var envelope = CreateTestEnvelope(receiverId: grainId, routeKey: "test-route");
        inbox.AddMessage(envelope);

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "inbox-processing-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, "run-1", 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(0, inbox.Count); // Inbox is empty
        await jobManager.DidNotReceive().ScheduleJobAsync(grainId, "inbox-processing-pump", Arg.Any<DateTimeOffset>(), null, Arg.Any<CancellationToken>());
    }
}
