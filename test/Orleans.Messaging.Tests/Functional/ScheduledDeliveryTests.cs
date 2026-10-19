using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Diagnostics;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.TestingHost.Diagnostics;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxRetries_UseJobTimeAndDeadLettersUseGrainTime(bool snapshot)
    {
        Fixture.Clock.Advance(TimeSpan.FromDays(2));
        var sender = NewGrain();
        var receiver = Fixture.Client.GetGrain<INoHandlerGrain>(Guid.NewGuid());
        await receiver.PingAsync();
        _ = await sender.GetSnapshotAsync();
        var options = Fixture.GetGrainContext(sender).ActivationServices
            .GetRequiredService<IOptions<DurableMessagingOptions>>().Value;
        using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);
        var firstRetry = WaitForRetryAsync(1);
        var startedAt = JobClock.GetUtcNow();
        var expiresAt = startedAt + options.OutboxRetryTimeout;

        var messageId = await sender.SendAsync(receiver.GetGrainId(), "messages/unhandled", NewMessage(61, "dead-letter"));
        await firstRetry;
        await AssertWakeupAtAsync(sender, startedAt + options.RetryInitialInterval);
        var state = await GetRetryStateAsync();
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(expiresAt, state.ExpirationTime);
        Assert.Equal(startedAt + options.RetryInitialInterval, state.NextAttemptAt);

        Fixture.Clock.Advance(TimeSpan.FromDays(1));
        await AssertWakeupAtAsync(sender, Assert.IsType<DateTimeOffset>(state.NextAttemptAt));
        Assert.Equal(1, (await GetRetryStateAsync()).AttemptCount);

        var secondRetry = WaitForRetryAsync(2);
        JobClock.Advance(options.RetryInitialInterval);
        _ = await Fixture.RunPumpAsync(sender);
        await secondRetry;
        state = await GetRetryStateAsync();
        Assert.Equal(expiresAt, state.ExpirationTime);
        Assert.Equal(JobClock.GetUtcNow() + 2 * options.RetryInitialInterval, state.NextAttemptAt);
        await AssertWakeupAtAsync(sender, Assert.IsType<DateTimeOffset>(state.NextAttemptAt));

        if (snapshot)
        {
            Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(sender.GetGrainId()));
            await sender.RetryWriteStateAsync();
        }
        await ReactivateAsync(sender);
        var recovered = await GetRetryStateAsync();
        Assert.Equal(state.AttemptCount, recovered.AttemptCount);
        Assert.Equal(state.ExpirationTime, recovered.ExpirationTime);
        Assert.Equal(state.NextAttemptAt, recovered.NextAttemptAt);
        await AssertWakeupAtAsync(sender, Assert.IsType<DateTimeOffset>(recovered.NextAttemptAt));

        var thirdRetry = WaitForRetryAsync(3);
        JobClock.Advance(expiresAt - JobClock.GetUtcNow() - TimeSpan.FromTicks(1));
        _ = await Fixture.RunPumpAsync(sender);
        await thirdRetry;
        await AssertWakeupAtAsync(sender, expiresAt);
        JobClock.Advance(TimeSpan.FromTicks(1));
        _ = await Fixture.RunPumpAsync(sender);
        var letter = Assert.Single((await Fixture.WaitForDeadLetterCountAsync(sender, 1)).OutboxDeadLetters);
        Assert.Equal(messageId, letter.MessageId);
        Assert.Equal(3, letter.AttemptCount);
        Assert.Equal(Fixture.Clock.GetUtcNow(), letter.DeadLetteredAt);

        if (snapshot)
        {
            Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(sender.GetGrainId()));
            await sender.RetryWriteStateAsync();
        }
        JobClock.Advance(options.DeadLetterRetentionPeriod);
        await ReactivateAsync(sender);
        Assert.Equal(letter, Assert.Single((await sender.GetSnapshotAsync()).OutboxDeadLetters));

        Fixture.Clock.Advance(options.DeadLetterRetentionPeriod - TimeSpan.FromTicks(1));
        await ReactivateAsync(sender);
        Assert.Equal(letter, Assert.Single((await sender.GetSnapshotAsync()).OutboxDeadLetters));
        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        await ReactivateAsync(sender);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
        await ReactivateAsync(sender);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);

        Task<DiagnosticEvent> WaitForRetryAsync(int attempt) => events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxRetryScheduled),
            entry => entry.Payload is MessagingEvents.OutboxRetryScheduled scheduled
                && scheduled.GrainContext.GrainId == sender.GetGrainId() && scheduled.AttemptCount == attempt,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Task<OutboxMessageState> GetRetryStateAsync() => Fixture.OnGrainAsync(sender, () =>
            Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<Outbox>().MessageState[messageId].State);
    }

    [Fact]
    public async Task OutboxDeadLetterInsertion_CompactsOnGrainTime()
    {
        Fixture.Clock.Advance(TimeSpan.FromDays(2));
        var sender = NewGrain();
        var receiver = Fixture.Client.GetGrain<INoHandlerGrain>(Guid.NewGuid());
        await receiver.PingAsync();
        _ = await sender.GetSnapshotAsync();
        var options = Fixture.GetGrainContext(sender).ActivationServices
            .GetRequiredService<IOptions<DurableMessagingOptions>>().Value;

        var first = Assert.Single((await DeadLetterAsync(62)).OutboxDeadLetters);
        Assert.Equal(Fixture.Clock.GetUtcNow(), first.DeadLetteredAt);
        JobClock.Advance(options.DeadLetterRetentionPeriod);
        var retained = (await DeadLetterAsync(63)).OutboxDeadLetters;
        Assert.Equal(2, retained.Count);
        Assert.Contains(first, retained);
        Assert.All(retained, letter => Assert.Equal(Fixture.Clock.GetUtcNow(), letter.DeadLetteredAt));

        Fixture.Clock.Advance(options.DeadLetterRetentionPeriod);
        var replacement = Assert.Single((await DeadLetterAsync(64)).OutboxDeadLetters);
        Assert.Equal(Fixture.Clock.GetUtcNow(), replacement.DeadLetteredAt);
        await ReactivateAsync(sender);
        Assert.Equal(replacement, Assert.Single((await sender.GetSnapshotAsync()).OutboxDeadLetters));

        async Task<EndpointSnapshot> DeadLetterAsync(int sequence)
        {
            using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);
            var retry = events.WaitForEventAsync(
                nameof(MessagingEvents.OutboxRetryScheduled),
                entry => entry.Payload is MessagingEvents.OutboxRetryScheduled scheduled
                    && scheduled.GrainContext.GrainId == sender.GetGrainId(),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var messageId = await sender.SendAsync(receiver.GetGrainId(), "messages/unhandled", NewMessage(sequence, "dead-letter"));
            await retry;
            JobClock.Advance(options.OutboxRetryTimeout);
            _ = await Fixture.RunPumpAsync(sender);
            return await Fixture.SnapshotProbe.WaitAsync(sender.GetGrainId(),
                state => state.OutboxCount == 0 && state.OutboxDeadLetters.Any(letter => letter.MessageId == messageId));
        }
    }

    [Fact]
    public async Task InboxWakeup_BareStagingWaitsForCommitAfterPriorWorkDrains()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        Assert.Equal(DeliveryStatus.Accepted,
            (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(70, "committed")))).Status);
        _ = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var pump = Fixture.GetPump(receiver);
        Assert.NotNull(await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.WakeupJobId(pump)));
        var attempts = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId());
        var staged = CreateEnvelope(receiver, NewMessage(71, "staged"));
        await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.StageAcceptance(Fixture.GetInbox(receiver), staged));

        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(attempts, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId()));
        var pending = await receiver.GetSnapshotAsync();
        Assert.Equal(1, pending.InboxCount);
        Assert.Equal("committed", Assert.Single(pending.Effects).Value);

        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        await receiver.RetryWriteStateAsync();
        Assert.Equal(attempts + 1, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId()));
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(new[] { "committed", "staged" }, completed.Effects.Select(effect => effect.Value));
        Assert.All(completed.Effects, effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task Wakeup_RetriesActivationFailuresBeyondTheDefaultLimitAndDeliversWithoutAClientCall()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var deliverAt = JobClock.GetUtcNow() + Delay;
        Assert.Equal(DeliveryStatus.Accepted,
            (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, NewMessage(80, "outage")), deliverAt))).Status);
        await Fixture.CollectAsync(Fixture.GetGrainContext(receiver));

        // Durable Jobs gives up on other jobs after 5 failed runs; the pump job keeps retrying through a longer outage.
        const int failures = 7;
        Fixture.Storage.FailReads(journal, failures);
        // Each retry becomes due on the Durable Jobs clock once Durable Jobs records the failed run.
        var reads = Fixture.Storage.GetReadCount(journal);
        await WaitUntilAsync(() =>
        {
            if (Fixture.Storage.GetReadCount(journal) > reads + failures) return true;
            JobClock.Advance(Delay);
            return false;
        });

        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("outage", Assert.Single(recovered.Effects).Value);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task Pump_StartsWhenActivationCompletesSoStagedWorkRunsWithoutAWakeup()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();

        // No messaging call has reached this activation, and its wake-up is suppressed.
        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        var staged = CreateEnvelope(receiver, NewMessage(83, "staged"));
        await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.StageAcceptance(Fixture.GetInbox(receiver), staged));
        await receiver.RetryWriteStateAsync();

        Assert.Equal("staged", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }
    [Fact]
    public async Task Wakeup_ForDueWorkIsScheduledOncePerIdlePeriodWithinTheRecheckDelay()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var grainId = receiver.GetGrainId();
        var scheduled = Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId).Count;
        var now = JobClock.GetUtcNow();
        using (var held = Fixture.HandlerProbe.Arm(grainId, HeldRoute))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(81, "first"), HeldRoute))).Status);
            await WaitForBarrierAsync(receiver, held);

            // The live loop performs due work, so the wake-up is crash insurance due within RecheckDelay, and later
            // writes reuse it.
            var jobs = Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId);
            Assert.Equal(scheduled + 1, jobs.Count);
            Assert.Equal(now + MessagingPump.RecheckDelay, jobs[^1].DueTime);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(82, "second")))).Status);
            Assert.Equal(scheduled + 1, Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId).Count);
        }

        var completed = await Fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(new[] { "first", "second" }, completed.Effects.Select(static effect => effect.Value));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task InboxWakeup_CapturedWriteRetainsCoverageUntilAcknowledged(bool snapshot, bool stored, bool lateStaging)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        if (snapshot) Fixture.Storage.RequestSnapshot(journal);
        var deliverAt = JobClock.GetUtcNow() + Delay;
        var accepted = Schedule(CreateEnvelope(receiver, NewMessage(72, "captured")), deliverAt);
        using var acknowledgement = stored ? Fixture.Storage.BlockAcknowledgement(journal) : Fixture.Storage.BlockWrite(journal);
        var delivery = DeliverAsync(receiver, accepted);
        await acknowledgement.WaitUntilEnteredAsync();
        Assert.False(delivery.IsCompleted);

        await AssertWakeupAtAsync(receiver, deliverAt);
        if (lateStaging)
        {
            var staged = CreateEnvelope(receiver, NewMessage(73, "later-staging"));
            await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.StageAcceptance(Fixture.GetInbox(receiver), staged));
            await AssertWakeupAtAsync(receiver, deliverAt);
        }
        Assert.Empty((await Fixture.OnGrainAsync(receiver, () => Fixture.GetSnapshot(receiver))).Effects);

        acknowledgement.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        await ReactivateAsync(receiver);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);
        await AssertWakeupAtAsync(receiver, deliverAt);
        JobClock.Advance(Delay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("captured", Assert.Single(completed.Effects).Value);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task InboxWakeup_CompletedJobDuringPreparationIsReplacedBeforeCapture()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;
        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        using var scheduling = Fixture.JobManagerProbe.BlockNext(MessagingPump.JobName);
        var delivery = DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, NewMessage(74, "prepared")), deliverAt));
        await scheduling.WaitUntilEnteredAsync();

        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.False(delivery.IsCompleted);
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));

        scheduling.Continue();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        Assert.Equal(2, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId()));
        Assert.Equal(deliverAt, Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, receiver.GetGrainId())).DueTime);
        await AssertWakeupAtAsync(receiver, deliverAt);
        JobClock.Advance(Delay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal("prepared", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

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

        // Any other wake-up completes in favour of the earliest one, whose run reschedules for the remaining work.
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunDuplicatePumpAsync(receiver)).Status);
        await AssertWakeupAtAsync(receiver, earlier);
        JobClock.Advance(earlier - JobClock.GetUtcNow());
        await AssertWakeupAtAsync(receiver, later);
        Assert.Equal("earlier", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunDuplicatePumpAsync(receiver)).Status);

        // Work due later than the earliest wake-up schedules nothing further.
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, NewMessage(22, "last")), later + Delay))).Status);
        Assert.Equal(2, Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, grainId).Count);
        await AssertWakeupAtAsync(receiver, later);
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

        // The recovered scheduled message counts toward MaxCapacity (2).
        var second = CreateEnvelope(receiver, NewMessage(4, "second"));
        using (var held = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, NewMessage(3, "held"), HeldRoute))).Status);
            await WaitForBarrierAsync(receiver, held);
            Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, second)).Status);
        }

        Assert.Equal("held", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, second)).Status);
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
    public async Task ScheduledMessages_CountTowardCapacityFromAcceptance()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;

        // MaxCapacity is 2, and scheduled messages hold capacity while they wait.
        for (var sequence = 10; sequence < 12; sequence++)
        {
            var scheduled = Schedule(CreateEnvelope(receiver, NewMessage(sequence, $"scheduled-{sequence}")), deliverAt);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, scheduled)).Status);
        }

        var laterScheduled = Schedule(CreateEnvelope(receiver, NewMessage(12, "scheduled-12")), deliverAt);
        var unscheduled = CreateEnvelope(receiver, NewMessage(20, "unscheduled"));
        var past = Schedule(CreateEnvelope(receiver, NewMessage(21, "past")), DateTimeOffset.MinValue);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, laterScheduled)).Status);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, unscheduled)).Status);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, past)).Status);
        Assert.Equal(2, (await receiver.GetSnapshotAsync()).InboxCount);

        JobClock.Advance(Delay);
        _ = await Fixture.RunPumpAsync(receiver);
        Assert.Equal(0, (await Fixture.WaitForEffectCountAsync(receiver, 2)).InboxCount);

        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, laterScheduled)).Status);
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, unscheduled)).Status);
        laterScheduled = laterScheduled with { SentAt = JobClock.GetUtcNow() };
        unscheduled = unscheduled with { SentAt = JobClock.GetUtcNow() };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, laterScheduled)).Status);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, unscheduled)).Status);
        var all = await Fixture.WaitForEffectCountAsync(receiver, 4);
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
    public async Task SelfSentScheduledMessage_IsAcceptedWithItsWriteAndProcessedWhenDue()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var deliverAt = JobClock.GetUtcNow() + Delay;
        var message = NewMessage(40, "self");
        var outgoing = TestApplicationProtocol.Create(Sessions, grain.GetGrainId(), grain.GetGrainId(), "messages/self", message);
        outgoing = new OutboxMessage { ReceiverId = outgoing.ReceiverId, Envelope = outgoing.Envelope with { DeliverAt = deliverAt } };

        await grain.StageOutputAsync(outgoing);
        await grain.RetryWriteStateAsync();

        // The grain's own inbox accepted the message in that write, without waiting for the due time.
        var delivered = await Fixture.SnapshotProbe.WaitAsync(
            grain.GetGrainId(), static snapshot => snapshot.InboxCount == 1 && snapshot.OutboxCount == 0);
        Assert.Empty(delivered.Effects);
        await AssertWakeupAtAsync(grain, deliverAt);
        Assert.Empty((await grain.GetSnapshotAsync()).Effects);

        JobClock.Advance(Delay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(grain)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(grain, 1);
        Assert.Equal("self", Assert.Single(completed.Effects).Value);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(0, completed.OutboxCount);
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
        message with { Envelope = message.Envelope with { DeliverAt = deliverAt } };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ScheduledDeliveryClusterFixture() : MessagingClusterFixture(1, receiverOnly: false)
    {
        public ManualJobClock JobClock { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureServices(IServiceCollection services) =>
            services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, JobClock);
    }
}
