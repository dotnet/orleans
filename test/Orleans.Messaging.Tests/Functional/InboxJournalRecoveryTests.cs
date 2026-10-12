using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxJournalRecoveryTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task FailedInboxAcceptance_FencesOldObjectsAndFreshReplayOmitsEnvelope()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var envelope = CreateEnvelope(receiver, NewMessage(2, "failed-acceptance"));

        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.NotSame(oldManager, Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<IJournaledStateManager>());
        Assert.Equal(0, recovered.InboxCount);
        Assert.Empty(recovered.Effects);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
    }

    [Fact]
    public async Task AmbiguousInboxAcceptanceCommit_PreservesAndProcessesRecoveredEnvelope()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        Fixture.Storage.FailAfterWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var envelope = CreateEnvelope(receiver, NewMessage(76, "ambiguous-acceptance"));

        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, completed.ActivationId);
        Assert.Equal("ambiguous-acceptance", Assert.Single(completed.Effects).Value);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task CompletionWriteFailure_RecoversInFreshActivation()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        const string route = "messages/inbox-completion-retry";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(57, "inbox-completion-retry"), route);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.Release();

        var cleaned = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId && snapshot.InboxCount == 0 && snapshot.Effects.Count > 0);

        Assert.Equal(1, Assert.Single(cleaned.Effects).Count);
        Assert.Empty(cleaned.InboxDeadLetters);
    }

    [Fact]
    public async Task WakeupSchedulingFailure_RetriedDeliveryProcessesOnce()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(61, "schedule-failure"));
        Fixture.JobManagerProbe.FailNext(MessagingPump.JobName);

        // The wakeup is scheduled while the acceptance write is prepared, so its failure prevents that commit.
        var failure = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => DeliverAsync(receiver, envelope));
        Assert.IsType<IOException>(failure.InnerException);
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var status = (await DeliverAsync(receiver, envelope)).Status;
        Assert.True(status is DeliveryStatus.Accepted or DeliveryStatus.Duplicate, status.ToString());
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
    }
}
