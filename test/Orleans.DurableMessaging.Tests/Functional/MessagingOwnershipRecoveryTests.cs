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
    public async Task SchedulingFailureBeforeEnqueue_RollsBackAcceptanceAndRetrySchedulesOnce()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(61, "schedule-failure"));
        Fixture.JobManagerProbe.FailNext(ReceiverTestServices.InboxJobName);

        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope.Value));
        var reverted = await receiver.GetSnapshotAsync();
        Assert.Equal(0, reverted.InboxCount);
        Assert.Null(reverted.InboxJobId);
        Assert.Null(reverted.InboxJob);
        Assert.Empty(reverted.Effects);
        Assert.Empty(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(2, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
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
