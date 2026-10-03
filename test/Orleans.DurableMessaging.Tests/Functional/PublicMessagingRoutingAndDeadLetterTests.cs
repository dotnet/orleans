using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class PublicMessagingRoutingAndDeadLetterTests : DurableMessagingBehaviorTestBase
{
    public PublicMessagingRoutingAndDeadLetterTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task RouteNotFound_OutboxRetriesThenDeadLettersWithoutReceiverPersistence()
    {
        var sender = NewGrain();
        var receiver = NewGrain();

        await sender.SendAsync(
            receiver.GetGrainId(),
            "unknown/outbox-route",
            NewMessage(72, "undeliverable"));
        var senderState = await Fixture.WaitForDeadLetterCountAsync(sender, 1);

        Assert.Equal(0, senderState.OutboxCount);
        var deadLetter = Assert.Single(senderState.OutboxDeadLetters);
        Assert.Equal("unknown/outbox-route", deadLetter.Route);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Contains("No handler", deadLetter.Reason, StringComparison.Ordinal);
        var receiverState = await receiver.GetSnapshotAsync();
        Assert.Equal(0, receiverState.InboxCount);
        Assert.Empty(receiverState.Effects);
        Assert.Empty(receiverState.InboxDeadLetters);
    }

    [Fact]
    public async Task OutboxDeadLetterRemoval_IsDurable()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var messageId = await sender.SendAsync(
            receiver.GetGrainId(),
            "unknown/removable-outbox-route",
            NewMessage(73, "removable-undeliverable"));
        _ = await Fixture.WaitForDeadLetterCountAsync(sender, 1);

        Assert.True(await sender.RemoveOutboxDeadLetterAsync(messageId));
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
        Assert.False(await sender.RemoveOutboxDeadLetterAsync(messageId));

        await sender.RequestDeactivationAsync();
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

}
