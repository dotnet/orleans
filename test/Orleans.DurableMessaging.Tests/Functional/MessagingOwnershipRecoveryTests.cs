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
public sealed class MessagingOwnershipRecoveryTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task RecoveryDuringInboxScheduling_PreventsFalseAcceptance()
    {
        var receiver = NewGrain();
        using var schedule = Fixture.JobManagerProbe.BlockNext("orleans.messaging.inbox-drain");
        using var envelope = CreateEnvelope(receiver, NewMessage(77, "recovered-during-schedule"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await schedule.WaitUntilEnteredAsync();
        await Fixture.RevertStateAsync(receiver);
        schedule.Continue();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => delivery);
        Assert.Contains("interrupted by state recovery", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, Fixture.GetSnapshot(receiver).InboxCount);
    }

    [Fact]
    public async Task FailedInboxAcceptance_RevertsEnvelopeAndOrphanedJobCannotProcess()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var depthBaseline = Fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth");
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var envelope = CreateEnvelope(receiver, NewMessage(2, "failed-acceptance"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => DeliverAsync(receiver, envelope.Value));

        var reverted = await receiver.GetSnapshotAsync();
        Assert.Equal(0, reverted.InboxCount);
        Assert.Empty(reverted.Effects);
        Assert.Equal(depthBaseline, Fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth"));
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(reverted.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Empty(recovered.Effects);
        Assert.Equal(depthBaseline, Fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(depthBaseline, Fixture.Metrics.GetDepth("orleans-durable-messaging-inbox-depth"));
    }

    [Fact]
    public async Task AmbiguousInboxAcceptanceCommit_PreservesAndProcessesRecoveredEnvelope()
    {
        var receiver = NewGrain();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        Fixture.Storage.FailAfterWrite(journalId);
        using var envelope = CreateEnvelope(receiver, NewMessage(76, "ambiguous-acceptance"));

        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope.Value));

        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("ambiguous-acceptance", Assert.Single(completed.Effects).Value);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task Inbox_PrecommitCrash_ReclaimsScheduledOrphanAfterRecovery()
    {
        const string jobName = "orleans.messaging.inbox-drain";
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        await receiver.DeactivateOnNextRecoveryAsync();
        var barrier = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var attemptBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-job-attempts-started");
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = Fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);
        Fixture.JobManagerProbe.DuplicateNext(jobName);
        using var envelope = CreateEnvelope(receiver, NewMessage(3, "inbox-orphan"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await barrier.WaitUntilEnteredAsync();
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-job-attempts-started",
            attemptBaseline + 2);

        Assert.Equal(
            orphanBaseline,
            Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
        barrier.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => delivery);

        var recovered = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            orphanBaseline + 2,
            jobName);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 2);

        Assert.Equal(0, recovered.InboxCount);
        Assert.Null(recovered.InboxJobId);
        Assert.Null(recovered.InboxJob);
        Assert.Empty(recovered.Effects);
        Assert.Equal(
            orphanBaseline + 2,
            Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
    }

    [Fact]
    public async Task CommittedOutbox_DeactivationBeforeLocalFollowUp_RecoversDurableJobOwnership()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAndDeactivateAsync(
            receiver.GetGrainId(),
            "messages/outbox-crash-window",
            NewMessage(52, "durable-wakeup"));
        await receiverWrite.WaitUntilEnteredAsync();
        var committed = Fixture.GetSnapshot(sender);

        Assert.Equal(1, committed.OutboxCount);
        Assert.False(string.IsNullOrEmpty(committed.OutboxJobId));
        var scheduledJob = Assert.Single(
            Fixture.JobManagerProbe.GetScheduledJobs("orleans.messaging.outbox-flush", sender.GetGrainId()));
        Assert.Equal(committed.OutboxJobId, scheduledJob.Metadata!["orleans.messaging.ownership-id"]);
        Assert.Equal(scheduledJob.Id, committed.OutboxJob?.Id);
        Assert.Equal(scheduledJob.ShardId, committed.OutboxJob?.ShardId);

        receiverWrite.Release();
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var recovered = await Fixture.SnapshotProbe.WaitAsync(
            sender.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId
                && snapshot.OutboxCount == 0
                && snapshot.OutboxJobId is null);

        Assert.Equal("durable-wakeup", Assert.Single(delivered.Effects).Value);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Null(recovered.OutboxJobId);
    }

    [Fact]
    public async Task Outbox_PrecommitCrash_ReclaimsScheduledOrphanAfterRecovery()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        await sender.DeactivateOnNextRecoveryAsync();
        var barrier = Fixture.Storage.BlockWrite(JournalId.FromGrainId(sender.GetGrainId()));
        var attemptBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-job-attempts-started");
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = Fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);
        Fixture.JobManagerProbe.DuplicateNext(jobName);

        var send = sender.SendAsync(
            receiver.GetGrainId(),
            "messages/outbox-orphan",
            NewMessage(54, "outbox-orphan"));
        await barrier.WaitUntilEnteredAsync();
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-job-attempts-started",
            attemptBaseline + 2);

        Assert.Equal(
            orphanBaseline,
            Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
        barrier.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => send);

        await sender.RequestDeactivationAsync();
        var recovered = await sender.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            orphanBaseline + 2,
            jobName);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 2);

        Assert.Equal(0, recovered.OutboxCount);
        Assert.Null(recovered.OutboxJobId);
        Assert.Null(recovered.OutboxJob);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        Assert.Equal(
            orphanBaseline + 2,
            Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
    }

    [Fact]
    public async Task OutboxJobClearWriteFailure_RevertsOwnershipAndRetryCleansUp()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var journalId = JournalId.FromGrainId(sender.GetGrainId());
        Fixture.Storage.FailWrite(journalId, matchingWrite: 3);

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/outbox-clear-retry",
            NewMessage(56, "outbox-clear-retry"));
        _ = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var cleaned = await Fixture.SnapshotProbe.WaitAsync(
            sender.GetGrainId(),
            static snapshot => snapshot.OutboxCount == 0 && snapshot.OutboxJobId is null);

        Assert.Equal(0, cleaned.OutboxCount);
        Assert.Null(cleaned.OutboxJobId);
        Assert.Null(cleaned.OutboxJob);
        await sender.RequestDeactivationAsync();
        var recovered = await sender.GetSnapshotAsync();
        Assert.Null(recovered.OutboxJobId);
        Assert.Equal(0, recovered.OutboxCount);
    }

    [Fact]
    public async Task InboxJobClearWriteFailure_RevertsThenRecoversAfterActivationLoss()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/inbox-clear-retry");
        using var envelope = CreateEnvelope(receiver, NewMessage(57, "inbox-clear-retry"), "messages/inbox-clear-retry");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()), matchingWrite: 2);
        Fixture.DeactivateOnNextRecovery(receiver);
        handler.Release();

        var recovered = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId);
        var cleaned = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.InboxCount == 0 && snapshot.InboxJobId is null);

        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(cleaned.Effects).Count);
        Assert.Empty(cleaned.InboxDeadLetters);
        Assert.Null(cleaned.InboxJobId);
    }

    [Fact]
    public async Task DeliveryIntoEmptyInbox_ReplacesStalePersistedJobOwnership()
    {
        var receiver = NewGrain();
        var staleJobId = $"stale-{Guid.NewGuid():N}";
        await receiver.SetInboxOwnershipAsync(
            staleJobId,
            new DurableJob
            {
                Id = $"stale-job-{Guid.NewGuid():N}",
                Name = "orleans.messaging.inbox-drain",
                DueTime = DateTimeOffset.UtcNow,
                TargetGrainId = receiver.GetGrainId(),
                ShardId = $"stale-shard-{Guid.NewGuid():N}",
                Metadata = new Dictionary<string, string>
                {
                    ["orleans.messaging.ownership-id"] = staleJobId
                }
            });
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/stale-owner");
        using var envelope = CreateEnvelope(receiver, NewMessage(58, "stale-owner"), "messages/stale-owner");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var accepted = Fixture.GetSnapshot(receiver);

        Assert.NotNull(accepted.InboxJobId);
        Assert.NotEqual(staleJobId, accepted.InboxJobId);

        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("stale-owner", Assert.Single(completed.Effects).Value);
    }

    [Fact]
    public async Task Inbox_StaleGenerationCompletesWithoutClearingNewerOwner()
    {
        var receiver = NewGrain();
        const string route = "messages/stale-inbox-generation";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(60, "newer-inbox-owner"), route);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var owned = Fixture.GetSnapshot(receiver);
        Assert.False(string.IsNullOrEmpty(owned.InboxJobId));
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var manager = Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        await manager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = receiver.GetGrainId(),
                JobName = "orleans.messaging.inbox-drain",
                DueTime = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["orleans.messaging.ownership-id"] = "0"
                }
            },
            TestContext.Current.CancellationToken);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 1);

        Assert.Equal(owned.InboxJobId, Fixture.GetSnapshot(receiver).InboxJobId);
        handler.Release();
        Assert.Equal("newer-inbox-owner", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

    [Fact]
    public async Task Outbox_StaleGenerationCompletesWithoutClearingNewerOwner()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/stale-outbox-generation",
            NewMessage(61, "newer-outbox-owner"));
        await receiverWrite.WaitUntilEnteredAsync();
        var owned = Fixture.GetSnapshot(sender);
        Assert.False(string.IsNullOrEmpty(owned.OutboxJobId));
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var manager = Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        try
        {
            await manager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = sender.GetGrainId(),
                    JobName = "orleans.messaging.outbox-flush",
                    DueTime = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["orleans.messaging.ownership-id"] = "0"
                    }
                },
                TestContext.Current.CancellationToken);
            await Fixture.Metrics.WaitForCountAsync(
                "orleans-durablejobs-jobs-completed",
                completionBaseline + 1);

            Assert.Equal(owned.OutboxJobId, Fixture.GetSnapshot(sender).OutboxJobId);
        }
        finally
        {
            receiverWrite.Release();
        }

        Assert.Equal("newer-outbox-owner", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

    [Fact]
    public async Task Outbox_JobVisibleDuringRecovery_PollsUntilCommittedOwnerIsRestored()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/recovery-visibility",
            NewMessage(62, "recovery-visibility"));
        await receiverWrite.WaitUntilEnteredAsync();
        var owned = Fixture.GetSnapshot(sender);
        var ownershipId = Assert.IsType<string>(owned.OutboxJobId);
        var recoveryRead = Fixture.Storage.BlockRead(JournalId.FromGrainId(sender.GetGrainId()));
        var recovery = Fixture.RevertStateAsync(sender).AsTask();
        await recoveryRead.WaitUntilEnteredAsync();
        var handlerBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-handler-executions-started");
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = Fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);
        var manager = Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        try
        {
            await manager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = sender.GetGrainId(),
                    JobName = jobName,
                    DueTime = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["orleans.messaging.ownership-id"] = ownershipId
                    }
                },
                TestContext.Current.CancellationToken);
            await Fixture.Metrics.WaitForCountAsync(
                "orleans-durablejobs-handler-executions-started",
                handlerBaseline + 1);

            Assert.Equal(
                orphanBaseline,
                Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
            Assert.Equal(completionBaseline, Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed"));
        }
        finally
        {
            recoveryRead.Release();
            await recovery;
            receiverWrite.Release();
        }

        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("recovery-visibility", Assert.Single(delivered.Effects).Value);
        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
    }

    [Fact]
    public async Task InboxSchedulingFailure_RevertsAcceptanceAndRetryDoesNotStrandMessage()
    {
        const string jobName = "orleans.messaging.inbox-drain";
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(59, "inbox-schedule-retry"));
        var scheduledBaseline = Fixture.JobManagerProbe.GetScheduledJobs(jobName, receiver.GetGrainId()).Count;
        Fixture.JobManagerProbe.FailAfterNext(jobName);

        await Assert.ThrowsAsync<IOException>(
            () => DeliverAsync(receiver, envelope.Value));
        Assert.Equal(0, (await receiver.GetSnapshotAsync()).InboxCount);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);

        var effect = Assert.Single(completed.Effects);
        Assert.Equal("inbox-schedule-retry", effect.Value);
        Assert.Equal(1, effect.Count);
        Assert.Equal(
            2,
            Fixture.JobManagerProbe.GetAttemptCount(
                "orleans.messaging.inbox-drain",
                receiver.GetGrainId()));
        Assert.Equal(
            2,
            Fixture.JobManagerProbe.GetSuccessCount(jobName, receiver.GetGrainId()));
        var scheduled = Fixture.JobManagerProbe.GetScheduledJobs(jobName, receiver.GetGrainId())
            .Skip(scheduledBaseline)
            .ToArray();
        Assert.Equal(2, scheduled.Length);
        Assert.NotEqual(scheduled[0].Id, scheduled[1].Id);
        Assert.NotEqual(
            scheduled[0].Metadata!["orleans.messaging.ownership-id"],
            scheduled[1].Metadata!["orleans.messaging.ownership-id"]);
    }
}
