using Microsoft.Extensions.DependencyInjection;
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

    // Attempts run at 0, 1, 3, 7, 15, 31 and 63 retry delays; the eighth and last runs when the timeout elapses.
    private static readonly TimeSpan RetryTimeout = RetryDelay * 100;

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
    public async Task TransientHandlerFailures_RetryUntilTheHandlerSucceedsWithinTheRetryTimeout()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var inbox = Fixture.GetInbox(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(196, "transient") with { ThrowForAttempts = 6 });
        var messageId = envelope.Envelope.MessageId;
        var start = Fixture.Clock.GetUtcNow();

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await Fixture.WaitUntilAsync(receiver, () => inbox.Count == 1 && InboxTestAccess.GetMessageState(inbox, messageId).AttemptCount == 1);
        await RunFailedAttemptsAsync(receiver, inbox, messageId, start, failedAttempts: 6);

        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    // Verifies each failed attempt's retry state, running the pump at each retry, and leaves the clock at the attempt
    // which follows the last failed one.
    private async Task RunFailedAttemptsAsync(IMessagingTestGrain receiver, Inbox inbox, MessageId messageId, DateTimeOffset start, int failedAttempts)
    {
        var expirationTime = start + RetryTimeout;
        for (var attempt = 1; ; attempt++)
        {
            var state = await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, messageId));
            Assert.Equal(attempt, state.AttemptCount);
            Assert.Equal(expirationTime, state.ExpirationTime);
            var backoff = start + RetryDelay * ((1 << attempt) - 1);
            Assert.Equal(backoff < expirationTime ? backoff : expirationTime, state.NextAttemptAt);
            Fixture.Clock.Advance(state.NextAttemptAt!.Value - Fixture.Clock.GetUtcNow());
            if (attempt == failedAttempts)
            {
                return;
            }

            _ = await Fixture.RunPumpAsync(receiver);
            await Fixture.WaitUntilAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, messageId).AttemptCount == attempt + 1);
        }
    }
    [Fact]
    public async Task OrdinaryHandlerFailures_RetryUntilTheRetryTimeoutThenDeadLetter()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var inbox = Fixture.GetInbox(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(193, "always-fails") with { ThrowDuringPreparation = true });
        var messageId = envelope.Envelope.MessageId;
        var start = Fixture.Clock.GetUtcNow();

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await Fixture.WaitUntilAsync(receiver, () => inbox.Count == 1 && InboxTestAccess.GetMessageState(inbox, messageId).AttemptCount == 1);

        // Backoff doubles per attempt, and the last attempt runs when InboxRetryTimeout elapses after the first failure.
        await RunFailedAttemptsAsync(receiver, inbox, messageId, start, failedAttempts: 7);
        Assert.Equal(start + RetryTimeout, Fixture.Clock.GetUtcNow());
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        var deadLettered = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        var deadLetter = Assert.Single(deadLettered.InboxDeadLetters);
        Assert.Equal(messageId, deadLetter.MessageId);
        Assert.Equal(8, deadLetter.AttemptCount);
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

        // Without a handler, the pending message holds no wake-up.
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        // Registering a handler on the same activation resumes the pending message.
        await receiver.ConfigureHandlerAsync(true);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(withoutHandler.ActivationId, completed.ActivationId);
        Assert.Equal("awaits-handler", Assert.Single(completed.Effects).Value);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    [Fact]
    public async Task WakeupWhoseJobCompletesBeforeSchedulingReturns_DoesNotSuppressLaterWakeups()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var pump = Fixture.GetPump(receiver);
        var target = receiver.GetGrainId();

        // The scheduled job runs and completes, with no work remaining, before the scheduling call returns.
        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        using var schedule = Fixture.JobManagerProbe.BlockNext(MessagingPump.JobName);
        var scheduling = await Fixture.OnGrainAsync(receiver, () => pump.EnsureWakeupAsync(DateTimeOffset.MinValue, TestContext.Current.CancellationToken).AsTask());
        await schedule.WaitUntilEnteredAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(receiver)).Status);
        schedule.Continue();
        await scheduling.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Later work schedules its own wake-up.
        var attempts = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, target);
        var envelope = CreateEnvelope(receiver, NewMessage(195, "later-work"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(attempts + 1, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, target));
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
    }

    private sealed class RetryFixture : MessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableMessagingOptions options)
        {
            base.ConfigureOptions(options);
            options.InboxRetryTimeout = RetryTimeout;
            options.RetryInitialInterval = RetryDelay;
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            UseTestClockForDurableJobs(services);
        }
    }
}
