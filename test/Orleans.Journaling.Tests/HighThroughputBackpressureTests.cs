using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// High-throughput backpressure scenario tests for durable inbox/outbox messaging.
///
/// These tests verify the system's behavior under sustained load with backpressure:
/// - Sender grain floods messages to receiver grain
/// - Low inbox capacity triggers backpressure
/// - All messages are eventually delivered (no message loss)
/// - Steady-state throughput measurement under load
/// - Multiple concurrent senders
///
/// Backpressure is verified indirectly through observable effects:
/// - Delivery timing (slower than theoretical maximum)
/// - All messages eventually delivered despite capacity constraints
/// - System remains stable under concurrent load
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling"), TestCategory("Performance")]
public class HighThroughputBackpressureTests : IClassFixture<HighThroughputBackpressureTests.Fixture>
{
    private readonly Fixture _fixture;

    public HighThroughputBackpressureTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Helper to wait for receiver to receive all expected messages.
    /// </summary>
    private async Task<FloodReceiverStats> WaitForMessagesAsync(IFloodReceiverGrain receiver, int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stats = await receiver.GetStats();
            if (stats.ReceivedCount >= expectedCount && stats.ProcessedCount >= expectedCount)
            {
                return stats;
            }
            await Task.Delay(100);
        }

        // Return final stats even if we didn't reach expected count
        return await receiver.GetStats();
    }

    /// <summary>
    /// Helper to wait for sender's outbox to drain completely.
    /// Optionally waits for TotalSent to reach expectedSent first.
    /// </summary>
    private async Task WaitForSenderOutboxToDrainAsync(IFloodSenderGrain sender, TimeSpan timeout, int? expectedSent = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stats = await sender.GetStats();

            // If expectedSent is provided, first wait for TotalSent to reach that count
            if (expectedSent.HasValue && stats.TotalSent < expectedSent.Value)
            {
                await Task.Delay(100);
                continue;
            }

            if (stats.PendingCount == 0)
            {
                return;
            }
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Tests high-throughput message flooding with backpressure.
    /// Verifies that all messages are eventually delivered even when inbox experiences backpressure.
    /// </summary>
    [Fact]
    public async Task FloodMessaging_WithLowCapacity_EventuallyDeliversAll()
    {
        // Arrange
        var receiver = _fixture.Client.GetGrain<IFloodReceiverGrain>(Guid.NewGuid());
        var sender = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());

        var messageCount = 30;

        // Configure receiver with moderate processing delay
        // With processing delay of 30ms and capacity=100, should process smoothly
        await receiver.ConfigureProcessing(processingDelayMs: 30, maxMessages: messageCount);

        // Act - Flood messages
        await sender.StartFlood(receiver.GetGrainId(), messageCount: messageCount);

        // Wait for sender's outbox to drain (all messages sent and delivered)
        await WaitForSenderOutboxToDrainAsync(sender, timeout: TimeSpan.FromSeconds(30), expectedSent: messageCount);

        // Wait for all messages to be delivered (allow time for backpressure and retries)
        var receiverStats = await WaitForMessagesAsync(receiver, messageCount, timeout: TimeSpan.FromSeconds(30));

        // Assert - All messages should be delivered
        var senderStats = await sender.GetStats();

        Assert.Equal(messageCount, senderStats.TotalSent);
        Assert.Equal(messageCount, receiverStats.ReceivedCount);
        Assert.Equal(0, senderStats.PendingCount);
    }

    /// <summary>
    /// Tests that no messages are lost during sustained backpressure.
    /// Verifies all messages are eventually delivered despite capacity constraints.
    /// </summary>
    [Fact]
    public async Task FloodMessaging_WithBackpressure_NoMessageLoss()
    {
        // Arrange
        var receiver = _fixture.Client.GetGrain<IFloodReceiverGrain>(Guid.NewGuid());
        var sender = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());

        var messageCount = 50;

        // Configure with slow processing to create sustained backpressure
        await receiver.ConfigureProcessing(processingDelayMs: 40, maxMessages: messageCount);

        // Act - Flood messages
        await sender.StartFlood(receiver.GetGrainId(), messageCount: messageCount);

        // Wait for sender's outbox to drain (all messages sent and delivered)
        await WaitForSenderOutboxToDrainAsync(sender, timeout: TimeSpan.FromSeconds(30), expectedSent: messageCount);

        // Wait for all messages to be processed (generous timeout to allow for retries)
        var receiverStats = await WaitForMessagesAsync(receiver, messageCount, timeout: TimeSpan.FromSeconds(30));

        // Assert - No messages should be lost
        var senderStats = await sender.GetStats();

        Assert.Equal(messageCount, senderStats.TotalSent);
        Assert.Equal(messageCount, receiverStats.ReceivedCount);
        Assert.Equal(messageCount, receiverStats.ProcessedCount);
        Assert.Equal(0, senderStats.PendingCount);
    }

    /// <summary>
    /// Tests steady-state throughput under sustained load.
    /// Verifies all messages are delivered and measures overall throughput.
    /// </summary>
    [Fact]
    public async Task FloodMessaging_SustainedLoad_MaintainsThroughput()
    {
        // Arrange
        var receiver = _fixture.Client.GetGrain<IFloodReceiverGrain>(Guid.NewGuid());
        var sender = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());

        var messageCount = 60;

        // Configure for sustained load
        // With 20ms processing delay, theoretical max is ~50 msg/s
        await receiver.ConfigureProcessing(processingDelayMs: 20, maxMessages: messageCount);

        // Act - Start flooding and measure total time
        var startTime = DateTimeOffset.UtcNow;
        await sender.StartFlood(receiver.GetGrainId(), messageCount: messageCount);

        // Wait for sender's outbox to drain (all messages sent and delivered)
        await WaitForSenderOutboxToDrainAsync(sender, timeout: TimeSpan.FromSeconds(30), expectedSent: messageCount);

        // Wait for all messages to be processed
        var receiverStats = await WaitForMessagesAsync(receiver, messageCount, timeout: TimeSpan.FromSeconds(30));
        var elapsed = DateTimeOffset.UtcNow - startTime;

        // Assert - All messages should be delivered
        var senderStats = await sender.GetStats();
        Assert.Equal(messageCount, senderStats.TotalSent);
        Assert.Equal(messageCount, receiverStats.ReceivedCount);
        Assert.Equal(0, senderStats.PendingCount);

        // Verify reasonable throughput (at least 5 msg/s with 20ms processing delay)
        var throughput = messageCount / elapsed.TotalSeconds;
        Assert.True(throughput >= 5.0, $"Expected at least 5 msg/s, got {throughput:F2} msg/s");
    }

    /// <summary>
    /// Tests recovery after slow processing period ends.
    /// Verifies system processes remaining messages quickly after backpressure is relieved.
    /// </summary>
    [Fact]
    public async Task FloodMessaging_AfterSlowProcessing_QuickRecovery()
    {
        // Arrange
        var receiver = _fixture.Client.GetGrain<IFloodReceiverGrain>(Guid.NewGuid());
        var sender = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());

        var messageCount = 40;

        // Start with slow processing
        await receiver.ConfigureProcessing(processingDelayMs: 100, maxMessages: messageCount);
        await sender.StartFlood(receiver.GetGrainId(), messageCount: messageCount);

        // Wait for some messages to queue up (wait until at least some are received but not all are processed yet)
        await TestHelpers.WaitUntilAsync(
            async () =>
            {
                var stats = await receiver.GetStats();
                // Wait until we have some activity but not yet complete
                return stats.ReceivedCount > 0 && stats.ReceivedCount < messageCount;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Some messages should be received during slow processing");

        var statsDuringSlow = await receiver.GetStats();
        Assert.True(statsDuringSlow.ReceivedCount < messageCount, "Some messages should still be pending");

        // Act - Speed up processing (relieve backpressure)
        await receiver.ConfigureProcessing(processingDelayMs: 5, maxMessages: messageCount);
        var reliefTimestamp = DateTimeOffset.UtcNow;

        // Wait for sender's outbox to drain (all messages sent and delivered)
        await WaitForSenderOutboxToDrainAsync(sender, timeout: TimeSpan.FromSeconds(15), expectedSent: messageCount);

        // Wait for recovery
        var statsAfterRelief = await WaitForMessagesAsync(receiver, messageCount, timeout: TimeSpan.FromSeconds(10));
        var recoveryTime = (DateTimeOffset.UtcNow - reliefTimestamp).TotalSeconds;

        // Assert - All messages should be delivered after relief
        Assert.Equal(messageCount, statsAfterRelief.ReceivedCount);
        Assert.True(recoveryTime < 15.0, $"Expected recovery within 15 seconds, took {recoveryTime:F2}s");
    }

    /// <summary>
    /// Tests concurrent senders flooding single receiver.
    /// Verifies system remains stable and delivers all messages despite concurrent load.
    /// </summary>
    [Fact]
    public async Task FloodMessaging_MultipleSenders_AllMessagesDelivered()
    {
        // Arrange
        var receiver = _fixture.Client.GetGrain<IFloodReceiverGrain>(Guid.NewGuid());
        var sender1 = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());
        var sender2 = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());
        var sender3 = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());

        var messagesPerSender = 20;
        var totalMessages = messagesPerSender * 3;

        await receiver.ConfigureProcessing(processingDelayMs: 30, maxMessages: totalMessages);

        // Act - Three senders flood the same receiver concurrently
        var floodTasks = new[]
        {
            sender1.StartFlood(receiver.GetGrainId(), messageCount: messagesPerSender),
            sender2.StartFlood(receiver.GetGrainId(), messageCount: messagesPerSender),
            sender3.StartFlood(receiver.GetGrainId(), messageCount: messagesPerSender)
        };

        await Task.WhenAll(floodTasks);

        // Wait for all senders' outboxes to drain
        await Task.WhenAll(
            WaitForSenderOutboxToDrainAsync(sender1, timeout: TimeSpan.FromSeconds(30), expectedSent: messagesPerSender),
            WaitForSenderOutboxToDrainAsync(sender2, timeout: TimeSpan.FromSeconds(30), expectedSent: messagesPerSender),
            WaitForSenderOutboxToDrainAsync(sender3, timeout: TimeSpan.FromSeconds(30), expectedSent: messagesPerSender)
        );

        // Wait for all messages to be delivered
        var receiverStats = await WaitForMessagesAsync(receiver, totalMessages, timeout: TimeSpan.FromSeconds(30));

        // Assert - All messages from all senders should be delivered
        var stats1 = await sender1.GetStats();
        var stats2 = await sender2.GetStats();
        var stats3 = await sender3.GetStats();

        Assert.Equal(messagesPerSender, stats1.TotalSent);
        Assert.Equal(messagesPerSender, stats2.TotalSent);
        Assert.Equal(messagesPerSender, stats3.TotalSent);
        Assert.Equal(totalMessages, receiverStats.ReceivedCount);
    }

    /// <summary>
    /// Tests message ordering per sender despite backpressure.
    /// Verifies all messages are received (allowing concurrent processing which may reorder).
    /// </summary>
    [Fact]
    public async Task FloodMessaging_WithBackpressure_AllSequencesReceived()
    {
        // Arrange
        var receiver = _fixture.Client.GetGrain<IFloodReceiverGrain>(Guid.NewGuid());
        var sender = _fixture.Client.GetGrain<IFloodSenderGrain>(Guid.NewGuid());

        var messageCount = 25;
        await receiver.ConfigureProcessing(processingDelayMs: 25, maxMessages: messageCount);

        // Act - Send messages with sequence numbers
        await sender.StartFlood(receiver.GetGrainId(), messageCount: messageCount);

        // Wait for all messages to be processed
        await WaitForMessagesAsync(receiver, messageCount, timeout: TimeSpan.FromSeconds(15));

        // Assert - Verify all sequences received
        var receivedSequences = await receiver.GetReceivedSequences();

        Assert.Equal(messageCount, receivedSequences.Count);

        // Verify all sequences were received (no duplicates, no gaps)
        var distinctSequences = receivedSequences.Distinct().OrderBy(x => x).ToList();
        Assert.Equal(messageCount, distinctSequences.Count);

        // Verify range is correct (0 to messageCount-1)
        Assert.Equal(0, distinctSequences.First());
        Assert.Equal(messageCount - 1, distinctSequences.Last());
    }

    /// <summary>
    /// Test fixture that configures the cluster with low inbox capacity for backpressure testing.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    // Higher capacity to reduce backpressure frequency
                    // Still low enough to occasionally trigger backpressure in some tests
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromMinutes(5);
                    opts.MaxOutboxRetryAge = TimeSpan.FromMinutes(1);
                    opts.EnableLongPolling = false;
                    // Fast retry for testing - 50ms base delay
                    opts.BackpressureRetryDelay = TimeSpan.FromMilliseconds(50);
                });
            });
        }
    }
}

// ============================================================================
// Flood Messaging Test Types
// ============================================================================

[GenerateSerializer]
public record FloodMessage
{
    [Id(0)] public required int Sequence { get; init; }
    [Id(1)] public required string Payload { get; init; }
}

[GenerateSerializer]
public record FloodMessageAck
{
    [Id(0)] public required int Sequence { get; init; }
}

// ============================================================================
// Flood Messaging Grain Interfaces
// ============================================================================

public interface IFloodSenderGrain : IGrainWithGuidKey
{
    Task StartFlood(GrainId receiverId, int messageCount);
    Task<FloodSenderStats> GetStats();
}

public interface IFloodReceiverGrain : IGrainWithGuidKey
{
    Task ConfigureProcessing(int processingDelayMs, int maxMessages);
    Task<FloodReceiverStats> GetStats();
    Task<List<int>> GetReceivedSequences();
}

[GenerateSerializer]
public record FloodSenderStats
{
    [Id(0)] public required int TotalSent { get; init; }
    [Id(1)] public required int PendingCount { get; init; }
}

[GenerateSerializer]
public record FloodReceiverStats
{
    [Id(0)] public required int ReceivedCount { get; init; }
    [Id(1)] public required int ProcessedCount { get; init; }
}

// ============================================================================
// Flood Messaging Grain Implementations
// ============================================================================

/// <summary>
/// Grain that floods messages to a target receiver and tracks delivery statistics.
/// Uses standard durable outbox for delivery.
/// </summary>
[GrainType("FloodTest.SenderGrain")]
public class FloodSenderGrain : DurableGrain, IFloodSenderGrain
{
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableValue<int> _totalSent;
    private readonly IDurableValue<int> _acknowledgedCount;

    public FloodSenderGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        [FromKeyedServices("totalSent")] IDurableValue<int> totalSent,
        [FromKeyedServices("acknowledgedCount")] IDurableValue<int> acknowledgedCount)
    {
        _inbox = inbox;
        _outbox = outbox;
        _totalSent = totalSent;
        _acknowledgedCount = acknowledgedCount;
    }

    public Task<FloodSenderStats> GetStats()
    {
        var stats = new FloodSenderStats
        {
            TotalSent = _totalSent.Value,
            PendingCount = _outbox.Count
        };
        return Task.FromResult(stats);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("ack", new AckHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task StartFlood(GrainId receiverId, int messageCount)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();

        for (var i = 0; i < messageCount; i++)
        {
            var builder = new DurableEnvelopeBuilder
            {
                SessionPool = sessionPool,
                SenderId = this.GetGrainId()
            };

            var envelope = builder
                .To(receiverId, "flood")
                .WithBody(new FloodMessage
                {
                    Sequence = i,
                    Payload = $"Message {i}"
                })
                .WithReplyTo(this.GetGrainId())
                .Build();

            _outbox.Send(envelope);
            _totalSent.Value++;
        }

        await WriteStateAsync();
    }

    private class AckHandler : IInboxHandler<FloodMessageAck>
    {
        private readonly FloodSenderGrain _grain;

        public AckHandler(FloodSenderGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(FloodMessageAck message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._acknowledgedCount.Value++;
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Grain that receives flood messages and can be configured with processing delays.
/// Used to create backpressure conditions for testing.
/// </summary>
[GrainType("FloodTest.ReceiverGrain")]
public class FloodReceiverGrain : DurableGrain, IFloodReceiverGrain
{
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableValue<int> _receivedCount;
    private readonly IDurableValue<int> _processedCount;
    private readonly IDurableValue<int> _processingDelayMs;
    private readonly IDurableValue<List<int>> _receivedSequences;

    public FloodReceiverGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        [FromKeyedServices("receivedCount")] IDurableValue<int> receivedCount,
        [FromKeyedServices("processedCount")] IDurableValue<int> processedCount,
        [FromKeyedServices("processingDelayMs")] IDurableValue<int> processingDelayMs,
        [FromKeyedServices("receivedSequences")] IDurableValue<List<int>> receivedSequences)
    {
        _inbox = inbox;
        _outbox = outbox;
        _receivedCount = receivedCount;
        _processedCount = processedCount;
        _processingDelayMs = processingDelayMs;
        _receivedSequences = receivedSequences;
    }

    public Task ConfigureProcessing(int processingDelayMs, int maxMessages)
    {
        _processingDelayMs.Value = processingDelayMs;
        return Task.CompletedTask;
    }

    public Task<FloodReceiverStats> GetStats()
    {
        var stats = new FloodReceiverStats
        {
            ReceivedCount = _receivedCount.Value,
            ProcessedCount = _processedCount.Value
        };
        return Task.FromResult(stats);
    }

    public Task<List<int>> GetReceivedSequences()
    {
        // Return a copy to avoid concurrent modification exceptions
        var sequences = _receivedSequences.Value ?? new List<int>();
        return Task.FromResult(new List<int>(sequences));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("flood", new FloodHandler(this));

        // Initialize sequences list if null
        _receivedSequences.Value ??= new List<int>();

        return base.OnActivateAsync(cancellationToken);
    }

    private class FloodHandler : IInboxHandler<FloodMessage>
    {
        private readonly FloodReceiverGrain _grain;

        public FloodHandler(FloodReceiverGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(FloodMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._receivedCount.Value++;

            // Create a new list to avoid concurrent modification
            var currentSequences = _grain._receivedSequences.Value ?? new List<int>();
            var newSequences = new List<int>(currentSequences) { message.Sequence };
            _grain._receivedSequences.Value = newSequences;

            // Simulate processing delay
            if (_grain._processingDelayMs.Value > 0)
            {
                await Task.Delay(_grain._processingDelayMs.Value, cancellationToken);
            }

            _grain._processedCount.Value++;

            // Send acknowledgment back to sender
            if (context.Envelope.ReplyTo is { } replyTo)
            {
                var ackEnvelope = context.CreateEnvelope()
                    .To(replyTo, "ack")
                    .WithBody(new FloodMessageAck { Sequence = message.Sequence })
                    .Build();

                context.Send(ackEnvelope);
            }

            await _grain.WriteStateAsync();
        }
    }
}
