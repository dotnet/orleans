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
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, old)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, current)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, fresh)).Status);
        Assert.All((await receiver.GetSnapshotAsync()).Effects, static effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task SnapshotCompaction_RemovesExpiredRecordsInterleavedWithRetainedOnesInOnePass()
    {
        // Compaction removes processed records while enumerating them, which Dictionary supports on the frameworks
        // Messaging targets. Completion order places expired records first and between retained ones.
        var receiver = NewGrain();
        var first = CreateEnvelope(receiver, NewMessage(140, "first-expired"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var kept = CreateEnvelope(receiver, NewMessage(141, "kept"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, kept)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 2);
        var middle = CreateEnvelope(receiver, NewMessage(142, "middle-expired")) with { SentAt = first.SentAt };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, middle)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 3);
        var alsoKept = CreateEnvelope(receiver, NewMessage(143, "also-kept"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, alsoKept)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 4);
        Assert.Equal(4, (await GetProcessedAsync(receiver)).Count);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        var fresh = CreateEnvelope(receiver, NewMessage(144, "fresh"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, fresh)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 5);

        var processed = await GetProcessedAsync(receiver);
        Assert.True(processed.SetEquals([kept.Envelope.MessageId, alsoKept.Envelope.MessageId, fresh.Envelope.MessageId]));
        var snapshot = await receiver.GetSnapshotAsync();
        Assert.Equal(3, snapshot.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, middle)).Status);
    }

    [Fact]
    public async Task SentAt_ExpiresOnlyAfterSnapshotAndRecoveryPreserveTheFloor()
    {
        var receiver = NewGrain();
        var message = CreateEnvelope(receiver, NewMessage(130, "expiring"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);

        Fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);

        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
        Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        await receiver.RetryWriteStateAsync();
        await ReactivateAsync(receiver);
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, message)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    private Task<HashSet<MessageId>> GetProcessedAsync(IMessagingTestGrain receiver)
    {
        var inbox = Fixture.GetInbox(receiver);
        return Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox));
    }
}
