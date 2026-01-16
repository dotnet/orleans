using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Integration tests for durable messaging recovery scenarios.
/// Verifies that message state (deduplication tracking) survives grain deactivation/reactivation.
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling")]
public class DurableMessagingRecoveryTests : IClassFixture<DurableMessagingRecoveryTests.Fixture>
{
    private readonly Fixture _fixture;

    public DurableMessagingRecoveryTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests that deduplication works across grain reactivation.
    /// Verifies that duplicate messages (same SenderId + MessageId) are rejected
    /// even after grain deactivation and reactivation.
    /// </summary>
    [Fact]
    public async Task Deduplication_WorksAcrossReactivation()
    {
        // Arrange
        var receiverGrain = _fixture.Client.GetGrain<IRecoveryTestGrain>(Guid.NewGuid());
        var extension = receiverGrain.AsReference<IDurableInboxExtension>();
        var senderGrainId = GrainId.Create("test-sender", Guid.NewGuid().ToString());

        // Act - Deliver first message
        var envelope1 = CreateTestEnvelope(
            receiverGrain.GetGrainId(),
            "TestRoute",
            new TestMessage { Content = "Duplicate test" },
            senderGrainId);

        var result1 = await extension.DeliverAsync(envelope1, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Accepted, result1.Status);

        // Wait for processing
        await Task.Delay(1000);

        // Deactivate the receiver
        var activationIdBefore = await receiverGrain.GetActivationId();
        await receiverGrain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(500);

        // Try to deliver duplicate message (same MessageId and SenderId) after reactivation
        var envelope2 = CreateTestEnvelope(
            receiverGrain.GetGrainId(),
            "TestRoute",
            new TestMessage { Content = "Duplicate test" },
            senderGrainId,
            envelope1.MessageId); // Same MessageId

        var result2 = await extension.DeliverAsync(envelope2, new DeliveryOptions(), CancellationToken.None);

        // Assert - Duplicate should be rejected, proving deduplication state survived
        Assert.Equal(DeliveryStatus.Duplicate, result2.Status);
        
        // Verify we got a different activation
        var activationIdAfter = await receiverGrain.GetActivationId();
        Assert.NotEqual(activationIdBefore, activationIdAfter);
    }

    /// <summary>
    /// Tests that outbox messages are delivered correctly.
    /// Verifies that messages sent via SendMessage reach the receiver.
    /// </summary>
    [Fact]
    public async Task OutboxDelivery_WorksCorrectly()
    {
        // Arrange
        var senderGrain = _fixture.Client.GetGrain<IRecoveryTestGrain>(Guid.NewGuid());
        var receiverGrain = _fixture.Client.GetGrain<IRecoveryTestGrain>(Guid.NewGuid());

        // Act - Send a message
        await senderGrain.SendMessage(
            receiverGrain.GetGrainId(),
            "TestRoute",
            new TestMessage { Content = "Outbox test message" });

        // Wait for delivery and processing
        await Task.Delay(1500);

        // Assert - Outbox should be empty after delivery
        var outboxCount = await senderGrain.GetOutboxCount();
        Assert.Equal(0, outboxCount);
        
        // Verify message was received and processed (inbox should be empty after processing)
        var inboxCount = await receiverGrain.GetInboxCount();
        Assert.Equal(0, inboxCount);
    }

    /// <summary>
    /// Tests that messages can be processed after grain reactivation.
    /// Verifies the inbox processing pump works correctly across grain lifecycle.
    /// </summary>
    [Fact]
    public async Task MessageProcessing_WorksAfterReactivation()
    {
        // Arrange
        var receiverGrain = _fixture.Client.GetGrain<IRecoveryTestGrain>(Guid.NewGuid());

        // Deliver a message, let it process
        var extension = receiverGrain.AsReference<IDurableInboxExtension>();
        var envelope1 = CreateTestEnvelope(
            receiverGrain.GetGrainId(),
            "TestRoute",
            new TestMessage { Content = "Message 1" });

        var result1 = await extension.DeliverAsync(envelope1, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Accepted, result1.Status);

        await Task.Delay(1000);

        // Deactivate the grain
        await receiverGrain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(500);

        // Send another message after reactivation
        var envelope2 = CreateTestEnvelope(
            receiverGrain.GetGrainId(),
            "TestRoute",
            new TestMessage { Content = "Message 2" });

        var result2 = await extension.DeliverAsync(envelope2, new DeliveryOptions(), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Accepted, result2.Status);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Both messages should have been processed (inbox empty)
        var inboxCount = await receiverGrain.GetInboxCount();
        Assert.Equal(0, inboxCount);
    }

    /// <summary>
    /// Helper method to create test envelopes.
    /// </summary>
    private DurableEnvelope CreateTestEnvelope(
        GrainId receiverId,
        string routeKey,
        TestMessage message,
        GrainId? senderId = null,
        Guid? messageId = null)
    {
        var sessionPool = _fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var actualSenderId = senderId ?? GrainId.Create("test-sender", Guid.NewGuid().ToString());

        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = actualSenderId
        };

        var envelopeBuilder = builder
            .To(receiverId, routeKey)
            .WithBody(message);

        var envelope = envelopeBuilder.Build();

        // If messageId is specified, create envelope with that ID for deduplication testing
        if (messageId.HasValue)
        {
            return new DurableEnvelope
            {
                MessageId = messageId.Value,
                SenderId = envelope.SenderId,
                ReceiverId = envelope.ReceiverId,
                RouteKey = envelope.RouteKey,
                Data = envelope.Data,
                CreatedAt = envelope.CreatedAt,
                CorrelationKey = envelope.CorrelationKey,
                ReplyTo = envelope.ReplyTo
            };
        }

        return envelope;
    }

    /// <summary>
    /// Test fixture that configures the cluster with durable messaging.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromDays(7);
                    opts.EnableLongPolling = false;
                    opts.DefaultPollTimeout = TimeSpan.FromSeconds(1);
                });
            });
        }
    }
}

// ============================================================================
// Test Message Types
// ============================================================================

[GenerateSerializer]
public record TestMessage
{
    [Id(0)] public required string Content { get; init; }
}

// ============================================================================
// Test Grain Interface
// ============================================================================

public interface IRecoveryTestGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task SendMessage(GrainId targetId, string routeKey, TestMessage message);
    Task<int> GetInboxCount();
    Task<int> GetOutboxCount();
}

// ============================================================================
// Test Grain Implementation
// ============================================================================

/// <summary>
/// Minimal test grain for recovery scenarios.
/// Uses built-in durable inbox/outbox without custom state tracking.
/// </summary>
public class RecoveryTestGrain : DurableGrain, IRecoveryTestGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;

    public RecoveryTestGrain(IDurableInbox inbox, IDurableOutbox outbox)
    {
        _inbox = inbox;
        _outbox = outbox;
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register a simple handler that just processes messages
        _inbox.RegisterHandler("TestRoute", new TestMessageHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SendMessage(GrainId targetId, string routeKey, TestMessage message)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(targetId, routeKey)
            .WithBody(message)
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public Task<int> GetInboxCount() => Task.FromResult(_inbox.Count);

    public Task<int> GetOutboxCount() => Task.FromResult(_outbox.Count);

    private class TestMessageHandler : IInboxHandler<TestMessage>
    {
        private readonly RecoveryTestGrain _grain;

        public TestMessageHandler(RecoveryTestGrain grain) => _grain = grain;

        public async ValueTask HandleAsync(TestMessage message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Just process the message - persist state to remove from inbox
            await _grain.WriteStateAsync();
        }
    }
}
