using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for OutboxDeliveryPump with DurableJobs integration.
/// </summary>
public class OutboxDeliveryPumpTests
{
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
    /// Test implementation of IDurableOutbox for unit testing.
    /// </summary>
    private sealed class TestOutbox : IDurableOutbox
    {
        private readonly Dictionary<Guid, DurableEnvelope> _messages = new();

        public int Count => _messages.Count;
        public IEnumerable<DurableEnvelope> Messages => _messages.Values;

        public void Send(DurableEnvelope envelope) => _messages[envelope.MessageId] = envelope;
        public bool RemoveMessage(Guid messageId) => _messages.Remove(messageId);
        public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) => _messages.TryGetValue(messageId, out envelope);
        public Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestInboxExtension : IDurableInboxExtension
    {
        private readonly DeliveryResult _result;

        public TestInboxExtension(DeliveryResult result)
        {
            _result = result;
        }

        public ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, DeliveryOptions options, CancellationToken cancellationToken = default)
        {
            return new ValueTask<DeliveryResult>(_result);
        }
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Assert
        Assert.NotNull(pump);
    }

    [Fact]
    public void Constructor_WithNullGrainFactory_ThrowsArgumentNullException()
    {
        // Arrange
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxDeliveryPump(null!, jobManager, outbox, stateMachineManager, logger, grainId));
    }

    [Fact]
    public void Constructor_WithNullJobManager_ThrowsArgumentNullException()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxDeliveryPump(grainFactory, null!, outbox, stateMachineManager, logger, grainId));
    }

    [Fact]
    public void Constructor_WithNullOutbox_ThrowsArgumentNullException()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OutboxDeliveryPump(grainFactory, jobManager, null!, stateMachineManager, logger, grainId));
    }

    [Fact]
    public async Task SchedulePumpAsync_WhenOutboxEmpty_ReturnsNull()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Act
        var result = await pump.SchedulePumpAsync();

        // Assert
        Assert.Null(result);
        await jobManager.DidNotReceive().ScheduleJobAsync(
            Arg.Any<GrainId>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SchedulePumpAsync_WhenOutboxHasMessages_SchedulesJob()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope();
        outbox.Send(envelope);

        var expectedJob = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };

        jobManager.ScheduleJobAsync(
            Arg.Any<GrainId>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>())
            .Returns(expectedJob);

        // Act
        var result = await pump.SchedulePumpAsync();

        // Assert
        Assert.NotNull(result);
        await jobManager.Received(1).ScheduleJobAsync(
            grainId,
            "outbox-delivery-pump",
            Arg.Any<DateTimeOffset>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithDeliveryAccepted_RemovesMessageFromOutbox()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope);

        // Setup mock inbox extension to return Accepted
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.Accepted()));
        
        grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(0, outbox.Count);
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithDeliveryDuplicate_RemovesMessageFromOutbox()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope);

        // Setup mock inbox extension to return Duplicate
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.Duplicate()));
        
        grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(0, outbox.Count);
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithDeliveryBackpressured_RetainsMessageInOutbox()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope);

        // Setup mock inbox extension to return Backpressured
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.Backpressured()));
        
        grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(1, outbox.Count); // Message should remain
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());

        // Verify pump is rescheduled with backoff
        await jobManager.Received(1).ScheduleJobAsync(
            grainId,
            "outbox-delivery-pump",
            Arg.Is<DateTimeOffset>(dt => dt > DateTimeOffset.UtcNow), // Future time (backoff)
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithDeliveryRouteNotFound_RemovesMessageFromOutbox()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope);

        // Setup mock inbox extension to return RouteNotFound
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.RouteNotFound("test-route")));
        
        grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(0, outbox.Count); // Message should be removed (cannot be delivered)
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithMultipleMessages_ProcessesAll()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add multiple messages to the outbox
        var envelope1 = CreateTestEnvelope(senderId: grainId);
        var envelope2 = CreateTestEnvelope(senderId: grainId);
        var envelope3 = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope1);
        outbox.Send(envelope2);
        outbox.Send(envelope3);

        // Setup mock inbox extension to return Accepted
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.Accepted()));
        
        grainFactory.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>()).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(0, outbox.Count); // All messages should be removed
        await stateMachineManager.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithBackpressuredMessages_ReschedulesWithExponentialBackoff()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope);

        // Setup mock inbox extension to return Backpressured
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.Backpressured()));
        
        grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act - first attempt
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert - verify rescheduled with backoff >= 1 second
        await jobManager.Received(1).ScheduleJobAsync(
            grainId,
            "outbox-delivery-pump",
            Arg.Is<DateTimeOffset>(dt => dt >= DateTimeOffset.UtcNow.AddSeconds(1)),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJobAsync_WithNoRemainingMessages_DoesNotReschedule()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var outbox = new TestOutbox();
        var stateMachineManager = Substitute.For<IStateMachineManager>();
        var logger = NullLogger<OutboxDeliveryPump>.Instance;
        var grainId = CreateTestGrainId();
        var pump = new OutboxDeliveryPump(grainFactory, jobManager, outbox, stateMachineManager, logger, grainId);

        // Add a message to the outbox
        var envelope = CreateTestEnvelope(senderId: grainId);
        outbox.Send(envelope);

        // Setup mock inbox extension to return Accepted
        var inboxExtension = Substitute.For<IDurableInboxExtension>();
        inboxExtension.DeliverAsync(
            Arg.Any<DurableEnvelope>(),
            Arg.Any<DeliveryOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DeliveryResult>(DeliveryResult.Accepted()));
        
        grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId).Returns(inboxExtension);

        // Create job context
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            Name = "outbox-delivery-pump",
            TargetGrainId = grainId,
            DueTime = DateTimeOffset.UtcNow,
            ShardId = Guid.Empty.ToString(),
            Metadata = null
        };
        var context = new TestDurableJobContext(job, Guid.NewGuid().ToString(), 1);

        // Act
        await pump.ExecuteJobAsync(context, CancellationToken.None);

        // Assert - verify no rescheduling occurred
        await jobManager.DidNotReceive().ScheduleJobAsync(
            grainId,
            "outbox-delivery-pump",
            Arg.Any<DateTimeOffset>(),
            null,
            Arg.Any<CancellationToken>());
    }
}
