using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// An activation which stops without recording an attempt's outcome, because a handler ended without an outcome or the
/// silo crashed, has its due messages charged one attempt at recovery, so backoff and InboxRetryTimeout bound every
/// failure path. This fixture's InboxRetryTimeout is zero.
/// </summary>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InterruptedAttemptTests : MessagingBehaviorTestBase
{
    private const string InterruptedReason = InboxTestAccess.InterruptedAttemptReason;

    [Fact]
    public async Task HandlerWhichAlwaysFailsAfterStaging_IsDeadLetteredOnceItsRetryWindowElapses()
    {
        var receiver = NewGrain();
        var envelope = CreateEnvelope(receiver, NewMessage(170, "stage-then-throw") with { ThrowAfterStaging = true });

        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));

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
    public async Task GracefulDeactivationWithPendingMessages_ChargesNoAttempt()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);

        // A scheduled message stays pending without an attempt, so the deactivation interrupts none.
        var envelope = CreateEnvelope(receiver, NewMessage(172, "graceful"));
        envelope = envelope with { Envelope = envelope.Envelope with { DeliverAt = Fixture.JobNow + TimeSpan.FromMinutes(1) } };
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
        await receiver.ConfigureHandlerAsync(false);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The message is due when the grain recovers, so only the journaled clean stop keeps recovery from charging it.
        Fixture.Clock.Advance(TimeSpan.FromMinutes(2));
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
