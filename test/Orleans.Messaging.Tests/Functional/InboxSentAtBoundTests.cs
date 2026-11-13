using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// Pins the order in which the inbox evaluates a delivery's send time: the future bound applies only to a time the
/// inbox would store, a recorded identity is answered from its record, and an untracked identity at or below the
/// durable admission floor expires.
/// </summary>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxSentAtBoundTests() : MessagingBehaviorTestBase(new SentAtBoundFixture())
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    // The bound is exact only on a clock which stands still between reading it and delivering.
    private ManualJobClock JobClock => ((SentAtBoundFixture)Fixture).JobClock;

    [Fact]
    public async Task SentAtBeyondTheBound_ThrowsWithoutStagingAndTheBoundItselfIsAdmitted()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var inbox = Fixture.GetInbox(receiver);
        var bound = Fixture.JobNow + Window;
        var message = CreateEnvelope(receiver, NewMessage(1, "future-bound")) with { SentAt = bound };
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DeliverAsync(receiver, message with { SentAt = bound.AddTicks(1) }));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetPendingMessages(inbox).Count));
        Assert.Equal(0, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedCount(inbox)));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        await receiver.RetryWriteStateAsync();
        writes = Fixture.Storage.GetSuccessfulWriteCount(journal);

        // A recorded identity may not extend its retention beyond the bound either.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DeliverAsync(receiver, message with { SentAt = bound.AddTicks(1) }));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(bound, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)));
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedIdentity_IsAnsweredFromItsRecordAfterClockRollbackPutsItBeyondTheBound(bool completed)
    {
        var receiver = NewGrain();
        const string route = "messages/sent-time-rollback";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var message = CreateEnvelope(receiver, NewMessage(2, "rolled-back"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await WaitForBarrierAsync(receiver, handler);
        if (completed)
        {
            handler.Release();
            await Fixture.WaitForInboxCountAsync(receiver, 0);
        }

        await receiver.RetryWriteStateAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var inbox = Fixture.GetInbox(receiver);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        JobClock.Advance(-(Window + TimeSpan.FromTicks(1)));
        Assert.True(message.SentAt > Fixture.JobNow + Window);

        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message with { SentAt = message.SentAt.AddTicks(-1) })).Status);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DeliverAsync(receiver, message with { SentAt = message.SentAt.AddTicks(1) }));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(message.SentAt, await Fixture.OnGrainAsync(receiver, () => completed
            ? InboxTestAccess.GetProcessedSentAt(inbox, message.Envelope.MessageId)
            : InboxTestAccess.GetPendingMessages(inbox)[message.Envelope.MessageId].Message.SentAt));

        handler.Release();
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task UntrackedIdentityAtTheDurableFloor_ExpiresAfterClockRollbackPutsItBeyondTheBound()
    {
        var receiver = NewGrain();
        var floor = Fixture.JobNow - Window;
        var advancing = CreateEnvelope(receiver, NewMessage(3, "advances-floor")) with { SentAt = floor };
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, advancing)).Status);
        var inbox = Fixture.GetInbox(receiver);
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));

        JobClock.Advance(-(Window + Window + TimeSpan.FromTicks(1)));
        var untracked = CreateEnvelope(receiver, NewMessage(4, "at-floor")) with { SentAt = floor };
        Assert.True(untracked.SentAt > Fixture.JobNow + Window);

        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, untracked)).Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DeliverAsync(receiver, untracked with { SentAt = floor.AddTicks(1) }));
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    private sealed class SentAtBoundFixture : MessagingClusterFixture
    {
        public ManualJobClock JobClock { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureOptions(DurableMessagingOptions options)
        {
            base.ConfigureOptions(options);
            options.DeduplicationWindow = Window;
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, JobClock);
        }
    }
}
