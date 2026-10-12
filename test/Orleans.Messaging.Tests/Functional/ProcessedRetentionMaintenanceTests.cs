using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class ProcessedRetentionMaintenanceTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task SnapshotWrite_PrunesExpiredProcessedIdsAndRecoveryRetainsCurrentOnes()
    {
        var receiver = NewGrain();
        var old = CreateEnvelope(receiver, NewMessage(120, "old"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, old)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var current = CreateEnvelope(receiver, NewMessage(121, "current"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, current)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 2);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        var fresh = CreateEnvelope(receiver, NewMessage(122, "fresh"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, fresh)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 3);

        var processed = await GetProcessedAsync(receiver);
        Assert.DoesNotContain(old.Envelope.MessageId, processed);
        Assert.Contains(current.Envelope.MessageId, processed);
        Assert.Contains(fresh.Envelope.MessageId, processed);

        await ReactivateAsync(receiver);
        var recovered = await GetProcessedAsync(receiver);
        Assert.True(processed.SetEquals(recovered));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, current)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, fresh)).Status);
    }

    [Fact]
    public async Task ExpiredProcessedId_IsAcceptedAgainWhileUnexpiredIdsDeduplicate()
    {
        var receiver = NewGrain();
        var message = CreateEnvelope(receiver, NewMessage(130, "expiring"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);

        Fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);

        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 2);
    }

    private Task<HashSet<HierarchicalKey>> GetProcessedAsync(IMessagingTestGrain receiver)
    {
        var inbox = Fixture.GetInbox(receiver);
        return Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox));
    }
}
