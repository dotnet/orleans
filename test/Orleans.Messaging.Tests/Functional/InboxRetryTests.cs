using Orleans.DurableJobs;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxRetryTests() : MessagingBehaviorTestBase(new RetryFixture())
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task HandlerFailure_RetriesAfterBackoffAndEarlyWakeupsAreHarmless()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var inbox = Fixture.GetInbox(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(192, "retry") with { ThrowOnceDuringPreparation = true });
        var messageId = envelope.Envelope.MessageId;
        var now = Fixture.Clock.GetUtcNow();

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await Fixture.WaitUntilAsync(receiver, () => inbox.Count == 1 && InboxTestAccess.GetMessageState(inbox, messageId).AttemptCount == 1);
        var state = await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, messageId));
        Assert.Equal(now + RetryDelay, state.NextAttemptAt);
        Assert.Contains("Injected handler preparation failure", state.LastError);

        // Wake-ups before the retry is due must not run the handler.
        var early = await Fixture.RunPumpAsync(receiver);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, early.Status);
        _ = await Fixture.RunPumpAsync(receiver);
        var pending = await receiver.GetSnapshotAsync();
        Assert.Equal(1, pending.InboxCount);
        Assert.Empty(pending.Effects);

        Fixture.Clock.Advance(RetryDelay);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
        Assert.Empty(completed.InboxDeadLetters);

        // Spurious wake-ups with no work complete immediately and change nothing.
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task OrdinaryHandlerFailures_DeadLetterAtExactConfiguredAttemptLimit()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var inbox = Fixture.GetInbox(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(193, "always-fails") with { ThrowDuringPreparation = true });
        var messageId = envelope.Envelope.MessageId;
        var start = Fixture.Clock.GetUtcNow();

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await Fixture.WaitUntilAsync(receiver, () => inbox.Count == 1 && InboxTestAccess.GetMessageState(inbox, messageId).AttemptCount == 1);

        // Backoff doubles per attempt.
        Fixture.Clock.Advance(RetryDelay);
        _ = await Fixture.RunPumpAsync(receiver);
        await Fixture.WaitUntilAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, messageId).AttemptCount == 2);
        var second = await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, messageId));
        Assert.Equal(start + RetryDelay + RetryDelay * 2, second.NextAttemptAt);

        Fixture.Clock.Advance(RetryDelay * 2);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        var deadLettered = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        var deadLetter = Assert.Single(deadLettered.InboxDeadLetters);
        Assert.Equal(messageId, deadLetter.MessageId);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Equal(0, deadLettered.InboxCount);
        Assert.Empty(deadLettered.Effects);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);

        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.Equal(deadLetter, Assert.Single(recovered.InboxDeadLetters));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    [Fact]
    public async Task AcceptedMessage_WaitsForHandlerRegistrationWithoutDeadLettering()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(194, "awaits-handler"), "messages/awaits-handler");
        using (var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/awaits-handler"))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
            await WaitForBarrierAsync(receiver, barrier);
            await receiver.ConfigureHandlerAsync(false);

            // Deactivation cancels the in-flight handler without consuming an attempt.
            await receiver.RequestDeactivationAsync();
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        var withoutHandler = await receiver.GetSnapshotAsync();
        Assert.Equal(1, withoutHandler.InboxCount);
        Assert.Empty(withoutHandler.Effects);
        Assert.Empty(withoutHandler.InboxDeadLetters);
        Assert.NotEqual(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        await receiver.ConfigureHandlerAsync(true);
        var noHandlerContext = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await noHandlerContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        _ = await Fixture.RunPumpAsync(receiver);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("awaits-handler", Assert.Single(completed.Effects).Value);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    private sealed class RetryFixture : MessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableMessagingOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxProcessingAttempts = 3;
            options.BackpressureRetryDelay = RetryDelay;
        }
    }
}
