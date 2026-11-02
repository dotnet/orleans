using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// An activation which stops without recording an attempt's outcome, because a handler failed after staging changes or
/// the silo crashed, has its due messages charged one attempt at recovery, so backoff and InboxRetryTimeout bound every
/// failure path. This fixture's InboxRetryTimeout is zero.
/// </summary>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InterruptedAttemptTests : MessagingBehaviorTestBase
{
    private const string InterruptedReason = "The activation stopped without recording the outcome of an attempt.";

    [Fact]
    public async Task HandlerWhichAlwaysFailsAfterStaging_IsDeadLetteredOnceItsRetryWindowElapses()
    {
        var receiver = NewGrain();
        var envelope = CreateEnvelope(receiver, NewMessage(170, "stage-then-throw") with { ThrowAfterStaging = true });

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);

        // The first recovery charges the interrupted attempt and starts the retry window; the rerun fails the same way,
        // and the next recovery dead-letters the message because the window has elapsed. No staged effect commits.
        var final = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        var letter = Assert.Single(final.InboxDeadLetters);
        Assert.Equal(envelope.Envelope.MessageId, letter.MessageId);
        Assert.Equal(InterruptedReason, letter.Reason);
        Assert.Equal(2, letter.AttemptCount);
        Assert.Empty(final.Effects);
        Assert.Equal(0, final.InboxCount);
    }

    [Fact]
    public async Task FailureOnAGrainWithUnreportableState_IsDeadLetteredOnceItsRetryWindowElapses()
    {
        const string processed = "orleans-messaging-inbox-messages-processed";
        var receiver = Fixture.Client.GetGrain<IPersistentStateMessagingGrain>(Guid.NewGuid());
        _ = await receiver.GetSnapshotAsync();
        var deadLettered = Fixture.Metrics.WaitForCountAsync(processed, Fixture.Metrics.GetCount(processed, "dead_lettered") + 1, "dead_lettered");
        var envelope = TestApplicationProtocol.CreateInbox(
            Sessions, GrainId.Create("external-test-sender", "persistent"), "messages/record", NewMessage(171, "unreportable"), Fixture.JobNow);

        Assert.Equal(DeliveryStatus.Accepted, (await receiver.AsReference<IMessagingExtension>()
            .DeliverAsync(envelope, TestContext.Current.CancellationToken)).Status);

        // The grain's persistent state can't report changes, so each failure discards in-memory state through recovery.
        // The charges still bound the retries: the second recovery dead-letters the message before activation completes,
        // so the snapshot reads the committed outcome.
        await deadLettered;
        var snapshot = await receiver.GetSnapshotAsync();

        Assert.Equal([InterruptedReason], snapshot.DeadLetterReasons);
        Assert.Equal([2], snapshot.DeadLetterAttempts);
        Assert.Equal(2, snapshot.Handled);
        Assert.Equal(0, snapshot.InboxCount);
    }

    [Fact]
    public async Task GracefulDeactivationWithPendingMessages_ChargesNoAttempt()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(172, "graceful"), "messages/graceful");
        using (var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/graceful"))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
            await WaitForBarrierAsync(receiver, barrier);
            await receiver.ConfigureHandlerAsync(false);

            // Deactivation cancels the in-flight attempt and journals a clean stop.
            await receiver.RequestDeactivationAsync();
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        _ = await receiver.GetSnapshotAsync();
        var inbox = Fixture.GetInbox(receiver);
        var state = await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, envelope.Envelope.MessageId));
        Assert.Equal(0, state.AttemptCount);
        Assert.Null(state.ExpirationTime);

        await receiver.ConfigureHandlerAsync(true);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Empty(completed.InboxDeadLetters);
    }
}
