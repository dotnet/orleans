using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class ScheduledDeliveryTests() : MessagingBehaviorTestBase(new ScheduledDeliveryClusterFixture())
{
    private const string HeldRoute = "messages/held";
    private static readonly TimeSpan Delay = TimeSpan.FromHours(1);

    private ManualJobClock JobClock => ((ScheduledDeliveryClusterFixture)Fixture).JobClock;

    [Fact]
    public async Task ScheduledMessage_IsHeldUntilDeliverAtThenProcessedOnce()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;
        var envelope = Schedule(CreateEnvelope(receiver, NewMessage(1, "scheduled")), deliverAt);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);

        // The acceptance is durable, but wake-ups before the due time leave the message pending and reschedule for it.
        await AssertWakeupAtAsync(receiver, deliverAt);
        await AssertWakeupAtAsync(receiver, deliverAt);
        var pending = await receiver.GetSnapshotAsync();
        Assert.Equal(1, pending.InboxCount);
        Assert.Empty(pending.Effects);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);

        JobClock.Advance(Delay - TimeSpan.FromTicks(1));
        await AssertWakeupAtAsync(receiver, deliverAt);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);

        JobClock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("scheduled", Assert.Single(completed.Effects).Value);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);

        // Late and duplicate wake-ups and redeliveries after completion change nothing.
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task EarlierScheduledMessage_SchedulesAnEarlierWakeupWhichSupersedesTheLaterOne()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var grainId = receiver.GetGrainId();
        var later = JobClock.GetUtcNow() + Delay;
        var earlier = JobClock.GetUtcNow() + TimeSpan.FromMinutes(1);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, NewMessage(20, "later")), later))).Status);
        var laterJob = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId));
        Assert.Equal(later, laterJob.DueTime);

        // The later wake-up does not cover a message due first, so its acceptance schedules an earlier one.
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, NewMessage(21, "earlier")), earlier))).Status);
        var jobs = Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(earlier, jobs[1].DueTime);

        // Work due later than the earliest wake-up schedules nothing further.
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, NewMessage(22, "last")), later + Delay))).Status);
        Assert.Equal(2, Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId).Count);

        // Any other wake-up completes in favour of the earliest one, whose run reschedules for the remaining work.
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunDuplicatePumpAsync(receiver)).Status);
        await AssertWakeupAtAsync(receiver, earlier);
        JobClock.Advance(earlier - JobClock.GetUtcNow());
        await AssertWakeupAtAsync(receiver, later);
        Assert.Equal("earlier", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunDuplicatePumpAsync(receiver)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledMessage_SurvivesReactivationAndRestoresCapacityAccounting(bool snapshot)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        if (snapshot)
        {
            Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        }

        var deliverAt = JobClock.GetUtcNow() + Delay;
        var envelope = Schedule(CreateEnvelope(receiver, NewMessage(2, "recovered")), deliverAt);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);

        await ReactivateAsync(receiver);

        var recovered = await receiver.GetSnapshotAsync();
        Assert.Equal(1, recovered.InboxCount);
        Assert.Empty(recovered.Effects);
        var inbox = Fixture.GetInbox(receiver);
        var recoveredDeliverAt = await Fixture.OnGrainAsync(receiver,
            () => InboxTestAccess.GetPendingMessages(inbox)[envelope.Envelope.MessageId].Message.Envelope.DeliverAt);
        Assert.Equal(deliverAt, recoveredDeliverAt);
        await AssertWakeupAtAsync(receiver, deliverAt);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);

        // The recovered scheduled message does not count toward MaxCapacity (2).
        using (var held = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(3, "held"), HeldRoute))).Status);
            await WaitForBarrierAsync(receiver, held);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(4, "second")))).Status);
            Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(5, "third")))).Status);
        }

        var unscheduled = await Fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.DoesNotContain(unscheduled.Effects, effect => effect.Value == "recovered");

        JobClock.Advance(Delay);
        _ = await Fixture.RunPumpAsync(receiver);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 3);
        Assert.Equal(new[] { "recovered", "held", "second" }, completed.Effects.Select(static effect => effect.Value));
        Assert.All(completed.Effects, static effect => Assert.Equal(1, effect.Count));
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task ScheduledMessages_DoNotCountTowardCapacityEvenWhenDue()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;

        // MaxCapacity is 2; scheduled messages are admitted beyond it.
        for (var sequence = 10; sequence < 13; sequence++)
        {
            var scheduled = Schedule(CreateEnvelope(receiver, NewMessage(sequence, $"scheduled-{sequence}")), deliverAt);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, scheduled)).Status);
        }

        var rejected = CreateEnvelope(receiver, NewMessage(22, "backpressured"));
        using (var held = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(20, "held"), HeldRoute))).Status);
            await WaitForBarrierAsync(receiver, held);

            // The held pass has not processed the scheduled messages, which are now due and still pending.
            JobClock.Advance(Delay);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(21, "second")))).Status);
            Assert.Equal(5, (await receiver.GetSnapshotAsync()).InboxCount);
            Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, rejected)).Status);

            // A due scheduled message is still accepted while unscheduled messages fill the inbox.
            var due = Schedule(CreateEnvelope(receiver, NewMessage(13, "scheduled-13")), JobClock.GetUtcNow());
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, due)).Status);
            Assert.Equal(6, (await receiver.GetSnapshotAsync()).InboxCount);
        }

        var completed = await Fixture.WaitForEffectCountAsync(receiver, 6);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, rejected)).Status);
        var all = await Fixture.WaitForEffectCountAsync(receiver, 7);
        Assert.All(all.Effects, static effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task ScheduledMessage_RedeliveryCoalescesAndDifferentDeliverAtConflicts()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;
        var envelope = Schedule(CreateEnvelope(receiver, NewMessage(30, "identity")), deliverAt);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(DeliveryStatus.Duplicate,
            (await DeliverAsync(receiver, Schedule(envelope, deliverAt.ToOffset(TimeSpan.FromHours(-7))))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(receiver, Schedule(envelope, deliverAt + TimeSpan.FromSeconds(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(receiver, Schedule(envelope, null)));
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);
        var inbox = Fixture.GetInbox(receiver);
        Assert.Equal(deliverAt, await Fixture.OnGrainAsync(receiver,
            () => InboxTestAccess.GetPendingMessages(inbox)[envelope.Envelope.MessageId].Message.Envelope.DeliverAt));

        JobClock.Advance(Delay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);

        // Completed IDs are deduplicated regardless of content.
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, Schedule(envelope, deliverAt + Delay))).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task ScheduledMessage_LeavesActivationCollectableUntilDue()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;
        var envelope = Schedule(CreateEnvelope(receiver, NewMessage(50, "collected")), deliverAt);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await AssertWakeupAtAsync(receiver, deliverAt);

        // Work which is due later does not keep the activation alive; the durable wake-up reactivates it.
        var context = Fixture.GetGrainContext(receiver);
        await Fixture.CollectAsync(context);

        var reactivated = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, reactivated.ActivationId);
        Assert.Equal(1, reactivated.InboxCount);
        JobClock.Advance(Delay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal("collected", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

    private async Task AssertWakeupAtAsync(IMessagingTestGrain grain, DateTimeOffset deliverAt)
    {
        var result = await Fixture.RunPumpAsync(grain);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, result.Status);
        Assert.Equal(deliverAt, result.RescheduleTime);
    }

    private static InboxMessage Schedule(InboxMessage message, DateTimeOffset? deliverAt) =>
        new() { Envelope = message.Envelope with { DeliverAt = deliverAt } };

    private sealed class ScheduledDeliveryClusterFixture() : MessagingClusterFixture(1, receiverOnly: true)
    {
        public ManualJobClock JobClock { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureServices(IServiceCollection services) =>
            services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, JobClock);
    }
}
