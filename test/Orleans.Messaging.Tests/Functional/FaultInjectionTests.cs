using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// Injects faults at each boundary of the delivery chain and asserts that transient infrastructure faults only delay a
/// message, which is then processed and delivered exactly once in effect, while only explicit rejections and expired
/// retries are permanent.
/// </summary>
/// <remarks>
/// Faults are injected through <see cref="DeliveryFaultInjector"/> for the outbox's call to the receiver,
/// <see cref="ControlledJournalStorageProvider"/> for journal writes, <see cref="DurableJobManagerProbe"/> for wake-up
/// scheduling, and <see cref="HandlerProbe"/> for handlers. Time moves only through
/// <see cref="FaultInjectionClusterFixture.JobClock"/>, and each retry runs through an explicit pump run.
/// </remarks>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class FaultInjectionTests : MessagingBehaviorTestBase
{
    private const string Route = "messages/record";
    private const string HeldRoute = "messages/held";
    private static readonly TimeSpan RetryInterval = FaultInjectionClusterFixture.RetryInterval;
    private static readonly TimeSpan RetryTimeout = FaultInjectionClusterFixture.OutboxRetryTimeout;
    private readonly FaultInjectionClusterFixture _cluster;

    public FaultInjectionTests() : this(new FaultInjectionClusterFixture())
    {
    }

    private FaultInjectionTests(FaultInjectionClusterFixture cluster) : base(cluster) => _cluster = cluster;

    private DeliveryFaultInjector Deliveries => _cluster.Deliveries;

    [Fact]
    public async Task OutboxWakeup_BareStagingWaitsForCommitAfterPriorDeliveryDrains()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        await sender.SendAsync(receiver.GetGrainId(), Route, NewMessage(20, "committed"));
        await Fixture.OnGrainAsync(sender, () => Fixture.GetPump(sender).EnsureStarted());
        await WaitForDrainedOutboxAsync(sender);
        var pump = Fixture.GetPump(sender);
        Assert.NotNull(await Fixture.OnGrainAsync(sender, () => InboxTestAccess.WakeupJobId(pump)));
        var attempts = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, sender.GetGrainId());
        await sender.StageWithoutCommitAsync(receiver.GetGrainId(), Route, NewMessage(21, "staged"));

        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(sender)).Status);
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(sender)).Status);
        Assert.Equal(attempts, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, sender.GetGrainId()));
        Assert.Equal(1, (await sender.GetSnapshotAsync()).OutboxCount);
        Assert.Equal(1, Deliveries.ReceiverCalls);

        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        await sender.RetryWriteStateAsync();
        Assert.Equal(attempts + 1, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, sender.GetGrainId()));
        await WaitForDrainedOutboxAsync(sender);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(new[] { "committed", "staged" }, completed.Effects.Select(effect => effect.Value));
        Assert.All(completed.Effects, effect => Assert.Equal(1, effect.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxWakeup_CapturedWriteRetainsCoverageUntilAcknowledgedAndRecovered(bool snapshot)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var previous = Fixture.GetGrainContext(sender);
        var journal = JournalId.FromGrainId(sender.GetGrainId());
        if (snapshot) Fixture.Storage.RequestSnapshot(journal);
        using var acknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        var message = NewMessage(22, "captured");
        var sending = sender.SendAsync(receiver.GetGrainId(), Route, message);
        await acknowledgement.WaitUntilEnteredAsync();
        Assert.False(sending.IsCompleted);
        Assert.Equal(0, Deliveries.ReceiverCalls);

        // Captured entries retain a wake-up while delivery awaits the write acknowledgement.
        var wakeup = await Fixture.RunPumpAsync(sender);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, wakeup.Status);
        Assert.Equal(_cluster.JobClock.GetUtcNow() + TimeSpan.FromSeconds(1), wakeup.RescheduleTime);
        Assert.Equal(0, Deliveries.ReceiverCalls);

        acknowledgement.Fail();
        await Assert.ThrowsAsync<IOException>(() => sending);
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await sender.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await WaitForDrainedOutboxAsync(sender);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Equal(1, Deliveries.ReceiverCalls);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
    }

    [Fact]
    public async Task OutboxWakeup_CompletedJobDuringPreparationIsReplacedBeforeCapture()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(sender.GetGrainId());
        using var write = Fixture.Storage.BlockWrite(journal);
        Fixture.JobManagerProbe.SkipNext(MessagingPump.JobName);
        using var scheduling = Fixture.JobManagerProbe.BlockNext(MessagingPump.JobName);
        var message = NewMessage(23, "prepared");
        var sending = sender.SendAsync(receiver.GetGrainId(), Route, message);
        await scheduling.WaitUntilEnteredAsync();

        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(sender)).Status);
        Assert.False(sending.IsCompleted);
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(journal));
        scheduling.Continue();
        await write.WaitUntilEnteredAsync();
        Assert.Equal(2, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, sender.GetGrainId()));
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, sender.GetGrainId()));
        Assert.Equal(0, Deliveries.ReceiverCalls);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await Fixture.RunPumpAsync(sender)).Status);

        write.Release();
        await sending;
        await WaitForDrainedOutboxAsync(sender);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Equal(1, Deliveries.ReceiverCalls);
    }

    [Theory]
    [InlineData(DeliveryFaultKind.Throw, "Injected transient delivery failure.")]
    [InlineData(DeliveryFaultKind.TimeOut, "TimeoutException")]
    public async Task TransientDeliveryFault_DelaysTheMessageThenDeliversItOnce(DeliveryFaultKind kind, string error)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Deliveries.Enqueue(kind == DeliveryFaultKind.Throw ? DeliveryFault.Throw() : DeliveryFault.TimeOut());
        var startedAt = _cluster.JobClock.GetUtcNow();
        var message = NewMessage(1, "transient");

        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);

        // The fault only schedules a retry; the receiver was never called.
        var attempt = await WaitForFailedAttemptAsync(sender, id, 1);
        Assert.Contains(error, attempt.LastError);
        Assert.Equal(startedAt + RetryInterval, attempt.NextAttemptAt);
        Assert.Equal(startedAt + RetryTimeout, attempt.ExpirationTime);
        Assert.Equal(0, Deliveries.ReceiverCalls);

        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);

        await WaitForDrainedOutboxAsync(sender);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    [Fact]
    public async Task LostResponse_AfterTheReceiverCommitted_IsADuplicateAtTheReceiverAndDeliveredAtTheSender()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var lost = DeliveryFault.LoseResponse();
        Deliveries.Enqueue(lost);
        var message = NewMessage(2, "lost-response");

        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);

        Assert.Equal(DeliveryStatus.Accepted, (await lost.ReceiverAnswer).Status);
        Assert.Contains("Injected loss of the receiver's response.", (await WaitForFailedAttemptAsync(sender, id, 1)).LastError);
        await Fixture.WaitForEffectCountAsync(receiver, 1);

        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);

        await WaitForDrainedOutboxAsync(sender);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Duplicate], Deliveries.Answers);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    [Fact]
    public async Task LostResponse_AfterTheSinkAccepted_RetiresTheSourceMessageAsDelivered()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var lost = DeliveryFault.LoseResponse();
        Deliveries.Enqueue(lost);
        var id = NewSourceMessageId("lost-response");
        var payload = RandomNumberGenerator.GetBytes(32);

        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);

        Assert.Equal(DeliveryStatus.Accepted, (await lost.ReceiverAnswer).Status);
        await WaitForFailedAttemptAsync(sender, id, 1);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);

        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), (await OnSinkAsync(sender, static grain => grain.GetState())).Retired[id.ToString()]);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Duplicate], Deliveries.Answers);
        Assert.Equal(payload, (await OnSinkAsync(receiver, static grain => grain.GetState())).Received[id.ToString()]);

        // The receiver's deduplication answered the retry before offering it to the sink again.
        var probe = _cluster.SourceSinkProbe.For(receiver);
        Assert.Equal(1, probe.SinkOffers);
        Assert.Equal(1, probe.SinkAcceptances);
    }

    [Theory]
    [InlineData(ReceiverRecovery.None)]
    [InlineData(ReceiverRecovery.Journal)]
    [InlineData(ReceiverRecovery.Snapshot)]
    public async Task LostResponse_AfterTheSinkRejected_RetiresTheSourceMessageAsRejected(ReceiverRecovery recovery)
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var probe = _cluster.SourceSinkProbe.For(receiver);
        probe.Mode = SinkMode.DeadLetter;

        // The rejection's write is the receiver's first, so a requested snapshot holds its processed record.
        if (recovery == ReceiverRecovery.Snapshot) Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        var lost = DeliveryFault.LoseResponse();
        Deliveries.Enqueue(lost);
        var id = NewSourceMessageId("lost-rejection");

        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, RandomNumberGenerator.GetBytes(32), write: true);

        var rejection = await lost.ReceiverAnswer;
        Assert.Equal(DeliveryStatus.DeadLettered, rejection.Status);
        await WaitForFailedAttemptAsync(sender, id, 1);
        if (recovery != ReceiverRecovery.None)
        {
            var previous = Fixture.GetGrainContext(receiver);
            await receiver.DeactivateAsync();
            await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        // The receiver answers the retry from its processed record, without offering the message to the sink again.
        probe.Mode = SinkMode.Accept;
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);

        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);
        var (retired, reasons, codes) = await OnSinkAsync(sender, static grain => (grain.GetState().Retired, grain.GetRetirementReasons(), grain.GetRetirementCodes()));
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Rejected), retired[id.ToString()]);
        Assert.Equal(SourceSinkGrain.Rejection, reasons[id.ToString()]);
        Assert.Equal(SourceSinkGrain.RejectionCode, codes[id.ToString()]);
        Assert.Equal([DeliveryStatus.DeadLettered, DeliveryStatus.DeadLettered], Deliveries.Answers);
        Assert.Equal(1, probe.SinkOffers);
        Assert.Equal(0, probe.SinkAcceptances);
    }

    public enum ReceiverRecovery
    {
        None,
        Journal,
        Snapshot
    }

    [Fact]
    public async Task TimedOutDelivery_WhoseCallIsStillCommitting_IsDeduplicatedAgainstTheRetry()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        using var acceptance = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var timeout = DeliveryFault.TimeOutInFlight();
        Deliveries.Enqueue(timeout);
        var message = NewMessage(3, "in-flight");

        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);

        // The sender gave up on the call while the receiver's acceptance write is still in progress, then retries.
        await acceptance.WaitUntilEnteredAsync();
        await WaitForFailedAttemptAsync(sender, id, 1);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await Deliveries.WaitForReceiverCallsAsync(2);
        acceptance.Release();

        var answers = await Deliveries.WaitForAnswersAsync(2);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Duplicate], answers.Order());
        Assert.Equal(DeliveryStatus.Accepted, (await timeout.ReceiverAnswer).Status);
        await WaitForDrainedOutboxAsync(sender);
        await AssertProcessedOnceAsync(receiver, message);
    }

    [Fact]
    public async Task PersistentDeliveryFault_DeadLettersAtTheSenderWithTheLastErrorAfterTheRetryTimeout()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        Deliveries.Repeat(static () => DeliveryFault.Throw(new IOException("Injected outage of the receiver's silo.")));
        var startedAt = _cluster.JobClock.GetUtcNow();
        var id = NewSourceMessageId("persistent-fault");

        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, RandomNumberGenerator.GetBytes(32), write: true);

        await WaitForFailedAttemptAsync(sender, id, 1);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        var second = await WaitForFailedAttemptAsync(sender, id, 2);
        Assert.Equal(startedAt + RetryTimeout, second.ExpirationTime);
        Assert.Equal(startedAt + RetryInterval + 2 * RetryInterval, second.NextAttemptAt);

        // Once the retry timeout passes, the next due attempt dead-letters the message without another try.
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryTimeout);

        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);
        var (retired, reasons, deadLetter) = await Fixture.OnGrainAsync(sender, () => (
            Sink(sender).GetState().Retired,
            Sink(sender).GetRetirementReasons(),
            _cluster.GetOutboxState(sender).DeadLetter(id)));
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), retired[id.ToString()]);
        Assert.Contains("Injected outage of the receiver's silo.", reasons[id.ToString()]);
        Assert.Equal(reasons[id.ToString()], deadLetter.Reason);
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Equal(2, Deliveries.Executions);
        Assert.Equal(0, Deliveries.ReceiverCalls);
        Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
    }

    [Fact]
    public async Task SinkBackpressure_ThroughTheOutbox_ExpiresAtTheOriginalAbsoluteDeadline()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var probe = _cluster.SourceSinkProbe.For(receiver);
        probe.Mode = SinkMode.Backpressure;
        var id = NewSourceMessageId("backpressure");
        var payload = RandomNumberGenerator.GetBytes(32);

        var sentAt = _cluster.JobClock.GetUtcNow();
        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);

        var first = await WaitForFailedAttemptAsync(sender, id, 1);
        Assert.Equal(sentAt + RetryTimeout, first.ExpirationTime);
        Assert.Equal("The receiver is backpressured.", first.LastError);

        // Backpressure never renews the logical send's bounded retry budget.
        var step = RetryTimeout * 3 / 4;
        await _cluster.AdvanceAndRunPumpAsync(sender, step);
        var second = await WaitForFailedAttemptAsync(sender, id, 2);
        Assert.Equal(sentAt + RetryTimeout, second.ExpirationTime);
        Assert.Empty(await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetterKeys.ToArray()));

        await _cluster.AdvanceAndRunPumpAsync(sender, RetryTimeout - step);
        Assert.Equal(sentAt + RetryTimeout, _cluster.JobClock.GetUtcNow());

        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), (await OnSinkAsync(sender, static grain => grain.GetState())).Retired[id.ToString()]);
        Assert.Equal([DeliveryStatus.Backpressured, DeliveryStatus.Backpressured], Deliveries.Answers);
        Assert.Equal(2, probe.SinkOffers);
        Assert.Equal(0, probe.SinkAcceptances);
        Assert.Empty((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
        var letter = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetter(id));
        Assert.Contains("retry timeout elapsed", letter.Reason, StringComparison.Ordinal);
        Assert.Empty(await OnSinkAsync(sender, static grain => grain.GetRetirementCodes()));
    }

    [Fact]
    public async Task DeliveryAttempts_KeepTheOriginalExpirationAsTheirCutoffAcrossRetryAndRecovery()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var probe = _cluster.SourceSinkProbe.For(receiver);
        probe.Mode = SinkMode.Backpressure;
        var id = NewSourceMessageId("redelivery-deadline");
        var sentAt = _cluster.JobClock.GetUtcNow();
        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, RandomNumberGenerator.GetBytes(32), write: true);
        await WaitForFailedAttemptAsync(sender, id, 1);
        var step = RetryInterval;
        await _cluster.AdvanceAndRunPumpAsync(sender, step);
        await WaitForFailedAttemptAsync(sender, id, 2);
        var beforeRecovery = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
        Assert.Equal(sentAt + RetryTimeout, beforeRecovery.ExpirationTime);
        var previous = Fixture.GetGrainContext(sender);
        Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(sender.GetGrainId()));
        await sender.WriteAsync();
        await sender.DeactivateAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, await sender.GetOutboxCountAsync());
        var recovered = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
        Assert.Equal(sentAt + RetryTimeout, recovered.ExpirationTime);
        probe.Mode = SinkMode.Accept;
        await _cluster.AdvanceAndRunPumpAsync(sender, 2 * step);
        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);

        // Transport timeouts and recovery cannot extend or change the immutable admission cutoff.
        Assert.Equal(
            [sentAt + RetryTimeout, sentAt + RetryTimeout, sentAt + RetryTimeout],
            Deliveries.Deadlines);
        Assert.Equal([DeliveryStatus.Backpressured, DeliveryStatus.Backpressured, DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Equal(1, probe.SinkAcceptances);
    }

    [Fact]
    public async Task RedeliveryDeadlineBeyondTheReceiversWindow_IsAcceptedWithoutAWindowRejection()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        Deliveries.Enqueue(DeliveryFault.ExtendRedeliveryDeadline(TimeSpan.FromDays(2)));
        var id = NewSourceMessageId("beyond-window");

        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, RandomNumberGenerator.GetBytes(32), write: true);

        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);
        var (retired, reasons, codes, deadLetters) = await Fixture.OnGrainAsync(sender, () => (
            Sink(sender).GetState().Retired,
            Sink(sender).GetRetirementReasons(),
            Sink(sender).GetRetirementCodes(),
            _cluster.GetOutboxState(sender).DeadLetterKeys.ToArray()));
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), retired[id.ToString()]);
        Assert.Empty(reasons);
        Assert.Empty(codes);
        Assert.Empty(deadLetters);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiverAcceptanceWriteFailure_IsRetriedBySenderAndProcessedOnce(bool ambiguous)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        if (ambiguous)
        {
            Fixture.Storage.FailAfterWrite(journal);
        }
        else
        {
            Fixture.Storage.FailWrite(journal);
        }

        var message = NewMessage(4, ambiguous ? "ambiguous-acceptance" : "failed-acceptance");

        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);

        // The receiver failed the call and deactivated; the sender holds the message for a retry.
        Assert.Contains("Injected", (await WaitForFailedAttemptAsync(sender, id, 1)).LastError);
        Assert.Empty(Deliveries.Answers);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);

        await WaitForDrainedOutboxAsync(sender);
        // An acceptance which became durable despite the failure is recovered, so the retry is a duplicate.
        Assert.Equal([ambiguous ? DeliveryStatus.Duplicate : DeliveryStatus.Accepted], Deliveries.Answers);
        var processed = await AssertProcessedOnceAsync(receiver, message);
        Assert.NotEqual(before.ActivationId, processed.ActivationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenderRetryStateWriteFailure_RecoversAndDeliversOnce(bool ambiguous)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var hold = DeliveryFault.HoldBeforeCall();
        Deliveries.Enqueue(hold);
        var startedAt = _cluster.JobClock.GetUtcNow();
        var message = NewMessage(5, ambiguous ? "ambiguous-retry-state" : "failed-retry-state");
        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);
        await hold.WaitUntilEnteredAsync();
        ArmSenderWriteFailure(sender, ambiguous);
        var previous = Fixture.GetGrainContext(sender);

        // The delivery fails, and so does the write which records the failed attempt.
        hold.Fail(new IOException("Injected delivery failure."));
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await sender.GetSnapshotAsync();

        if (ambiguous)
        {
            // The recorded attempt became durable, so the recovered message waits for its backoff.
            var recovered = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
            Assert.Equal(1, recovered.AttemptCount);
            Assert.Equal(startedAt + RetryInterval, recovered.NextAttemptAt);
            await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        }

        await WaitForDrainedOutboxAsync(sender);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Equal(1, Deliveries.ReceiverCalls);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenderRemovalWriteFailure_AfterTheReceiverAccepted_DeliversOnceInEffect(bool ambiguous)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var hold = DeliveryFault.HoldAfterCall();
        Deliveries.Enqueue(hold);
        var message = NewMessage(6, ambiguous ? "ambiguous-removal" : "failed-removal");
        _ = await sender.SendAsync(receiver.GetGrainId(), Route, message);
        await hold.WaitUntilEnteredAsync();
        Assert.Equal(DeliveryStatus.Accepted, (await hold.ReceiverAnswer).Status);
        ArmSenderWriteFailure(sender, ambiguous);
        var previous = Fixture.GetGrainContext(sender);

        hold.Release();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await sender.GetSnapshotAsync();

        await WaitForDrainedOutboxAsync(sender);
        // A removal which never became durable is redelivered, and the receiver reports the duplicate.
        DeliveryStatus[] expected = ambiguous ? [DeliveryStatus.Accepted] : [DeliveryStatus.Accepted, DeliveryStatus.Duplicate];
        Assert.Equal(expected, Deliveries.Answers);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    [Fact]
    public async Task SenderRemovalWriteFailure_RecoveredAfterTheRetryTimeout_DeadLettersWithoutRedelivery()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var hold = DeliveryFault.HoldAfterCall();
        Deliveries.Enqueue(hold);
        var message = NewMessage(7, "late-recovery");
        _ = await sender.SendAsync(receiver.GetGrainId(), Route, message);
        await hold.WaitUntilEnteredAsync();
        Assert.Equal(DeliveryStatus.Accepted, (await hold.ReceiverAnswer).Status);
        ArmSenderWriteFailure(sender, ambiguous: false);
        var previous = Fixture.GetGrainContext(sender);
        hold.Release();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The recovered entry's retry timeout runs from its send, so an attempt now could reach the receiver after its
        // deduplication window. The outbox dead-letters it instead of redelivering it.
        _cluster.JobClock.Advance(RetryTimeout);
        var snapshot = await Fixture.WaitForDeadLetterCountAsync(sender, 1);

        var deadLetter = Assert.Single(snapshot.OutboxDeadLetters);
        Assert.Contains("retry timeout elapsed", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Equal(0, snapshot.OutboxCount);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        await AssertProcessedOnceAsync(receiver, message);
    }

    [Fact]
    public async Task HeldReceiver_DelaysOnlyItsOwnMessage()
    {
        var sender = NewGrain();
        var slow = NewGrain();
        var fast = NewGrain();
        var held = DeliveryFault.HoldBeforeCall();
        Deliveries.Enqueue(held);
        var slowMessage = NewMessage(20, "slow");
        var fastMessage = NewMessage(21, "fast");

        _ = await sender.SendAsync(slow.GetGrainId(), Route, slowMessage);
        await held.WaitUntilEnteredAsync();
        _ = await sender.SendAsync(fast.GetGrainId(), Route, fastMessage);

        // Attempts complete independently, so the message to the other receiver is delivered while the first is held.
        await AssertProcessedOnceAsync(fast, fastMessage);
        Assert.Equal(1, (await sender.GetSnapshotAsync()).OutboxCount);

        held.Release();
        await WaitForDrainedOutboxAsync(sender);
        await AssertProcessedOnceAsync(slow, slowMessage);
        Assert.Equal(2, Deliveries.Executions);
    }

    [Fact]
    public async Task UnknownDeliveryStatus_IsRetriedAsAnUnknownOutcome()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Deliveries.Enqueue(DeliveryFault.Answer(new DeliveryResult { Status = (DeliveryStatus)99 }));
        var message = NewMessage(22, "unknown-status");

        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);

        // A newer receiver's status which this sender can't interpret may hide an acceptance, so it retries.
        Assert.Contains("Unexpected delivery status '99'", (await WaitForFailedAttemptAsync(sender, id, 1)).LastError);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await WaitForDrainedOutboxAsync(sender);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    [Fact]
    public async Task Withdrawal_DuringAnAttempt_IgnoresTheAttemptsOutcome()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var hold = DeliveryFault.HoldAfterCall();
        Deliveries.Enqueue(hold);
        var id = NewSourceMessageId("withdrawn-in-flight");

        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, RandomNumberGenerator.GetBytes(32), write: true);
        await hold.WaitUntilEnteredAsync();
        await sender.WithdrawAsync(id);
        await sender.WriteAsync();

        // The attempt already reached the receiver, but the withdrawn entry neither retires nor delivers again.
        hold.Release();
        var outbox = Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<Outbox>();
        await Fixture.WaitUntilAsync(sender, () => !outbox.IsDelivering);
        Assert.Empty((await OnSinkAsync(sender, static grain => grain.GetState())).Retired);
        Assert.Equal(0, await Fixture.OnGrainAsync(sender, () => outbox.Count));
        Assert.Empty(await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetterKeys.ToArray()));
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Equal(1, Deliveries.Executions);
    }
    [Fact]
    public async Task SenderCrash_AfterTheReceiverCommitted_RedeliversAsADuplicate()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var hold = DeliveryFault.HoldAfterCall();
        Deliveries.Enqueue(hold);
        var message = NewMessage(7, "sender-crash");
        _ = await sender.SendAsync(receiver.GetGrainId(), Route, message);
        await hold.WaitUntilEnteredAsync();
        Assert.Equal(DeliveryStatus.Accepted, (await hold.ReceiverAnswer).Status);

        var wakeup = await Fixture.RunPumpAsync(sender);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, wakeup.Status);
        Assert.Equal(_cluster.JobClock.GetUtcNow() + TimeSpan.FromSeconds(1), wakeup.RescheduleTime);
        Assert.Equal(1, Deliveries.ReceiverCalls);

        // Deactivation cancels the in-flight delivery, whose outcome is never applied or counted as an attempt.
        await _cluster.CrashAsync(sender);
        _ = await sender.GetSnapshotAsync();

        await WaitForDrainedOutboxAsync(sender);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Duplicate], Deliveries.Answers);
        await AssertProcessedOnceAsync(receiver, message);
        Assert.Empty((await sender.GetSnapshotAsync()).OutboxDeadLetters);
    }

    [Fact]
    public async Task ReceiverCrash_MidBatch_RecoveryProcessesEachMessageOnce()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var deliverAt = _cluster.JobClock.GetUtcNow() + RetryTimeout;
        var first = NewMessage(8, "first");
        var held = NewMessage(9, "held");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, first, "messages/first"), deliverAt))).Status);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, Schedule(CreateEnvelope(receiver, held, HeldRoute), deliverAt))).Status);
        var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute);

        // Both messages become due in one batch; the first is handled and the second is in its handler at the crash.
        _cluster.JobClock.Advance(RetryTimeout);
        var run = Fixture.RunPumpAsync(receiver);
        await WaitForBarrierAsync(receiver, barrier);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        await _cluster.CrashAsync(receiver);
        barrier.Dispose();
        _ = run.ContinueWith(static task => task.Exception, TaskScheduler.Default);

        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.Effects.Count == 2 && snapshot.InboxCount == 0);
        Assert.All(recovered.Effects, static effect => Assert.Equal(1, effect.Count));
        Assert.Equal([first.LogicalId, held.LogicalId], recovered.Effects.Select(static effect => effect.LogicalId));
        Assert.Empty(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task CanceledPumpRun_KeepsItsJobAndTheActivationKeepsProcessing()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute);
        var message = NewMessage(10, "canceled-run");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, message, HeldRoute))).Status);
        await WaitForBarrierAsync(receiver, barrier);
        var attempts = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId());

        // Durable Jobs cancels a run only when its shard moves, and the job stays stored for the next owner, so the
        // activation schedules nothing and keeps working.
        using var cancellation = new CancellationTokenSource();
        var run = Fixture.RunCancelablePumpAsync(receiver, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(attempts, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId()));

        barrier.Dispose();
        var processed = await AssertProcessedOnceAsync(receiver, message);
        Assert.Equal(before.ActivationId, processed.ActivationId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledDeliveryWakeupSchedulingFailure_IsRetriedBySenderAndProcessedAtDeliverAt(bool scheduledBeforeFailure)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var receiverContext = Fixture.GetGrainContext(receiver);
        var hold = DeliveryFault.HoldBeforeCall();
        Deliveries.Enqueue(hold);
        var deliverAt = _cluster.JobClock.GetUtcNow() + RetryTimeout + RetryTimeout;
        var message = NewMessage(11, "scheduled");
        var outgoing = TestApplicationProtocol.Create(Sessions, sender.GetGrainId(), receiver.GetGrainId(), Route, message);
        outgoing = outgoing with { Envelope = outgoing.Envelope with { DeliverAt = deliverAt } };
        await sender.StageOutputAsync(outgoing);
        await sender.RetryWriteStateAsync();
        await hold.WaitUntilEnteredAsync();

        // The receiver's acceptance write schedules its wake-up for the message, and the scheduling fails.
        if (scheduledBeforeFailure)
        {
            Fixture.JobManagerProbe.FailAfterNext(MessagingPump.JobName);
        }
        else
        {
            Fixture.JobManagerProbe.FailNext(MessagingPump.JobName);
        }

        hold.Release();
        Assert.Contains("Injected", (await WaitForFailedAttemptAsync(sender, outgoing.Envelope.MessageId, 1)).LastError);
        await receiverContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(Deliveries.Answers);

        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await WaitForDrainedOutboxAsync(sender);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        var pending = await Fixture.WaitForInboxCountAsync(receiver, 1);
        Assert.Empty(pending.Effects);

        await _cluster.AdvanceAndRunPumpAsync(receiver, RetryTimeout + RetryTimeout);
        await AssertProcessedOnceAsync(receiver, message);
    }

    [Fact]
    public async Task OutboxDepth_IsReleasedByDeactivationAndRestoredByRecovery()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Deliveries.Enqueue(DeliveryFault.Throw());
        var message = NewMessage(12, "depth-across-activations");
        var id = await sender.SendAsync(receiver.GetGrainId(), Route, message);
        _ = await WaitForFailedAttemptAsync(sender, id, 1);
        await Fixture.WaitForOutboxDepthAsync(1);

        await _cluster.CrashAsync(sender);
        await Fixture.WaitForOutboxDepthAsync(0);

        // Recovery reports the pending message again, and retiring it releases that depth.
        Assert.Equal(1, (await sender.GetSnapshotAsync()).OutboxCount);
        await Fixture.WaitForOutboxDepthAsync(1);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await WaitForDrainedOutboxAsync(sender);
        await Fixture.WaitForOutboxDepthAsync(0);
        await AssertProcessedOnceAsync(receiver, message);
    }

    [Fact]
    public async Task OutboxDepth_AfterAFailedWrite_CountsOnlyDurableMessages()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Deliveries.Enqueue(DeliveryFault.Throw());
        var durable = NewMessage(13, "durable");
        var id = await sender.SendAsync(receiver.GetGrainId(), Route, durable);
        _ = await WaitForFailedAttemptAsync(sender, id, 1);
        await Fixture.WaitForOutboxDepthAsync(1);
        var previous = Fixture.GetGrainContext(sender);
        ArmSenderWriteFailure(sender, ambiguous: false);

        // The failed write fences the activation, which releases the depth of both messages.
        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(receiver.GetGrainId(), Route, NewMessage(14, "lost")));
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Fixture.WaitForOutboxDepthAsync(0);

        Assert.Equal(1, (await sender.GetSnapshotAsync()).OutboxCount);
        await Fixture.WaitForOutboxDepthAsync(1);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await WaitForDrainedOutboxAsync(sender);
        await Fixture.WaitForOutboxDepthAsync(0);
        await AssertProcessedOnceAsync(receiver, durable);
    }

    [Fact]
    public async Task EquivalentDuplicate_OfACommittedMessage_LeavesItsDeliveryUnchanged()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Deliveries.Enqueue(DeliveryFault.Throw());
        var message = NewMessage(15, "duplicate-after-commit");
        var outgoing = TestApplicationProtocol.Create(Sessions, sender.GetGrainId(), receiver.GetGrainId(), Route, message);
        var id = outgoing.Envelope.MessageId;
        await sender.StageOutputAsync(outgoing);
        await sender.RetryWriteStateAsync();
        var attempt = await WaitForFailedAttemptAsync(sender, id, 1);
        var before = await sender.GetSnapshotAsync();

        // The grain call delivers a copy of the envelope, which is equivalent to the committed one.
        await sender.StageOutputAsync(outgoing);
        await sender.RetryWriteStateAsync();

        Assert.Single(Fixture.GetStagedOutput(sender));
        var unchanged = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
        Assert.Equal(attempt.AttemptCount, unchanged.AttemptCount);
        Assert.Equal(attempt.NextAttemptAt, unchanged.NextAttemptAt);
        Assert.Equal(attempt.ExpirationTime, unchanged.ExpirationTime);
        Assert.Equal(before.ActivationId, (await sender.GetSnapshotAsync()).ActivationId);

        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await WaitForDrainedOutboxAsync(sender);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Equal(1, Deliveries.ReceiverCalls);
        await AssertProcessedOnceAsync(receiver, message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingDuplicate_IsRejectedWithoutChangingThePendingMessage(bool commitFirst)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        Deliveries.Enqueue(DeliveryFault.Throw());
        var message = NewMessage(16, "original");
        var outgoing = TestApplicationProtocol.Create(Sessions, sender.GetGrainId(), receiver.GetGrainId(), Route, message);
        var id = outgoing.Envelope.MessageId;
        var conflicting = TestApplicationProtocol.Create(
            Sessions, sender.GetGrainId(), receiver.GetGrainId(), Route, message with { Value = "conflicting" });
        Assert.Equal(id, conflicting.Envelope.MessageId);
        await sender.StageOutputAsync(outgoing);
        if (commitFirst)
        {
            await sender.RetryWriteStateAsync();
            _ = await WaitForFailedAttemptAsync(sender, id, 1);
        }

        var before = await sender.GetSnapshotAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.StageOutputAsync(conflicting));
        Assert.Contains(id.ToString(), error.Message);
        Assert.Equal(outgoing.Envelope.Payload, Assert.Single(Fixture.GetStagedOutput(sender)).Envelope.Payload);
        if (!commitFirst)
        {
            await sender.RetryWriteStateAsync();
            _ = await WaitForFailedAttemptAsync(sender, id, 1);
        }

        Assert.Equal(before.ActivationId, (await sender.GetSnapshotAsync()).ActivationId);
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await WaitForDrainedOutboxAsync(sender);
        Assert.Equal([DeliveryStatus.Accepted], Deliveries.Answers);
        var processed = await AssertProcessedOnceAsync(receiver, message);
        Assert.Equal(message.Value, Assert.Single(processed.Effects).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionFloor_ForwardThenBackwardClockRemainsFencedAcrossRecovery(bool snapshot)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var originalTime = _cluster.JobClock.GetUtcNow();
        _cluster.JobClock.Advance(TimeSpan.FromDays(2));
        var floor = _cluster.JobClock.GetUtcNow() - TimeSpan.FromDays(1);
        var expired = CreateEnvelope(receiver, NewMessage(80, "floor-equality")) with { RedeliveryDeadline = floor };
        if (snapshot) Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        var result = await DeliverAsync(receiver, expired);
        Assert.Equal(DeliveryStatus.Expired, result.Status);
        Assert.Null(result.Code);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);

        _cluster.JobClock.Advance(originalTime - _cluster.JobClock.GetUtcNow());
        await ReactivateAsync(receiver);
        var inbox = Fixture.GetInbox(receiver);
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.AdmissionFloor(inbox)));
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        foreach (var delta in new[] { -1L, 0L })
        {
            var fenced = CreateEnvelope(receiver, NewMessage(81, "fenced")) with { RedeliveryDeadline = floor.AddTicks(delta) };
            Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, fenced)).Status);
        }

        var available = CreateEnvelope(receiver, NewMessage(82, "above-floor")) with { RedeliveryDeadline = floor.AddTicks(1) };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, available)).Status);
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        var effect = Assert.Single((await receiver.GetSnapshotAsync()).Effects);
        Assert.Equal("above-floor", effect.Value);
        Assert.Equal(1, effect.Count);
        Assert.Equal(floor.AddTicks(1), await Fixture.OnGrainAsync(receiver, () =>
            InboxTestAccess.GetProcessedCutoff(inbox, available.Envelope.MessageId)));
    }

    [Fact]
    public async Task AdmissionFloor_AtMinimumClock_ClampsWithoutUnderflow()
    {
        var receiver = NewSinkGrain();
        _cluster.JobClock.Advance(DateTimeOffset.MinValue - _cluster.JobClock.GetUtcNow());
        var boundary = new InboxMessage
        {
            Envelope = new() { MessageId = NewSourceMessageId("minimum"), Payload = [85], Subject = SourceSinkGrain.Subject },
            RedeliveryDeadline = DateTimeOffset.MinValue
        };
        Assert.Equal(DeliveryStatus.Expired, (await receiver.AsReference<IMessagingExtension>()
            .DeliverAsync(boundary, TestContext.Current.CancellationToken)).Status);
        var firstAvailable = boundary with
        {
            Envelope = boundary.Envelope with { MessageId = NewSourceMessageId("first-tick"), Payload = [86] },
            RedeliveryDeadline = DateTimeOffset.MinValue.AddTicks(1)
        };
        Assert.Equal(DeliveryStatus.Accepted, (await receiver.AsReference<IMessagingExtension>()
            .DeliverAsync(firstAvailable, TestContext.Current.CancellationToken)).Status);
        var received = Assert.Single((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
        Assert.Equal(firstAvailable.Envelope.MessageId.ToString(), received.Key);
        Assert.Equal([86], received.Value);
        Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
    }

    [Fact]
    public async Task SnapshotFloor_OmittedEvidenceRemainsUntilTheSnapshotAcknowledgement()
    {
        var receiver = NewGrain();
        var cutoff = _cluster.JobClock.GetUtcNow() + RetryTimeout;
        var message = CreateEnvelope(receiver, NewMessage(83, "snapshot-fence")) with { RedeliveryDeadline = cutoff };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await Fixture.WaitForInboxCountAsync(receiver, 0);
        var inbox = Fixture.GetInbox(receiver);
        var oldFloor = await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox));
        _cluster.JobClock.Advance(TimeSpan.FromDays(2));
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        Fixture.Storage.RequestSnapshot(journal);
        using var acknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        var writing = receiver.RetryWriteStateAsync();
        await acknowledgement.WaitUntilEnteredAsync();

        Assert.False(writing.IsCompleted);
        Assert.Contains(message.Envelope.MessageId, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        Assert.Equal(oldFloor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        acknowledgement.Release();
        await writing;
        Assert.DoesNotContain(message.Envelope.MessageId, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        Assert.Equal(_cluster.JobClock.GetUtcNow() - TimeSpan.FromDays(1),
            await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, message)).Status);
        await ReactivateAsync(receiver);
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, message)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task ProcessedHistory_FloorOnlyAndOrdinaryAcksRetainItUntilAnUnchangedFloorSnapshotCommits()
    {
        var receiver = NewSinkGrain();
        var started = _cluster.JobClock.GetUtcNow();
        var finite = Enumerable.Range(0, 8).Select(index => new InboxMessage
        {
            Envelope = new() { MessageId = NewSourceMessageId($"finite-{index}"), Payload = [(byte)index], Subject = SourceSinkGrain.Subject },
            RedeliveryDeadline = started + RetryTimeout + TimeSpan.FromMinutes(index)
        }).ToArray();
        var legacy = Enumerable.Range(0, 8).Select(index => new InboxMessage
        {
            Envelope = new() { MessageId = NewSourceMessageId($"null-{index}"), Payload = [(byte)(index + 8)], Subject = SourceSinkGrain.Subject }
        }).ToArray();
        var future = new InboxMessage
        {
            Envelope = new() { MessageId = NewSourceMessageId("unfenced-finite"), Payload = [16], Subject = SourceSinkGrain.Subject },
            RedeliveryDeadline = started + TimeSpan.FromDays(10)
        };
        var extension = receiver.AsReference<IMessagingExtension>();
        foreach (var message in finite.Concat(legacy).Append(future))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(message, TestContext.Current.CancellationToken)).Status);
        }

        var inbox = Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        var retained = finite.Concat(legacy).Append(future).Select(static message => message.Envelope.MessageId).ToHashSet();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        _cluster.JobClock.Advance(TimeSpan.FromDays(2));
        DateTimeOffset floor = default;
        for (var index = 0; index < 32; index++)
        {
            _cluster.JobClock.Advance(TimeSpan.FromTicks(1));
            floor = _cluster.JobClock.GetUtcNow() - TimeSpan.FromDays(1);
            var stale = new InboxMessage
            {
                Envelope = new() { MessageId = NewSourceMessageId($"floor-only-{index}"), Payload = [], Subject = SourceSinkGrain.Subject },
                RedeliveryDeadline = floor
            };
            Assert.Equal(DeliveryStatus.Expired, (await extension.DeliverAsync(stale, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(writes + index + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
            Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
            // These finite records are already eligible. Their continued presence proves scalar acks did not compact.
            Assert.True(retained.SetEquals(await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox))));
        }

        var ordinary = new InboxMessage
        {
            Envelope = new() { MessageId = NewSourceMessageId("ordinary-append"), Payload = [17], Subject = SourceSinkGrain.Subject }
        };
        Assert.Equal(DeliveryStatus.Accepted, (await extension.DeliverAsync(ordinary, TestContext.Current.CancellationToken)).Status);
        retained.Add(ordinary.Envelope.MessageId);
        Assert.Equal(writes + 33, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.True(retained.SetEquals(await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox))));

        // The floor is already durable. This snapshot must reclaim history even though its candidate cannot advance.
        _cluster.JobClock.Advance(started - _cluster.JobClock.GetUtcNow());
        Fixture.Storage.RequestSnapshot(journal);
        using var acknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        var snapshot = receiver.WriteAsync();
        await acknowledgement.WaitUntilEnteredAsync();
        Assert.False(snapshot.IsCompleted);
        Assert.Equal(writes + 34, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.AdmissionFloor(inbox)));
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        Assert.True(retained.SetEquals(await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox))));
        acknowledgement.Release();
        await snapshot;

        retained.ExceptWith(finite.Select(static message => message.Envelope.MessageId));
        Assert.True(retained.SetEquals(await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox))));
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        await _cluster.CrashAsync(receiver);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        inbox = Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        Assert.True(retained.SetEquals(await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox))));
        foreach (var message in finite)
        {
            Assert.Equal(DeliveryStatus.Expired, (await extension.DeliverAsync(message, TestContext.Current.CancellationToken)).Status);
        }
        foreach (var message in legacy.Append(future).Append(ordinary))
        {
            Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(message, TestContext.Current.CancellationToken)).Status);
        }
        Assert.Equal(18, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        Assert.Equal(18, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResponse_AfterCompactionAndClockRollback_CannotApplyTheMessageTwice(bool realSink)
    {
        var sender = NewSinkGrain();
        IAddressable receiver = realSink ? NewSinkGrain() : NewGrain();
        var id = NewSourceMessageId("late-lost-response");
        var payload = realSink
            ? RandomNumberGenerator.GetBytes(32)
            : TestApplicationProtocol.Encode(Sessions, new TestApplicationMessage(Route, new TestMessage(id, 84, "late-redelivery")));
        using var events = new DeliveryEventObserver(sender.GetGrainId(), receiver.GetGrainId(), id, faults: 0);
        var retryScheduled = events.RetryScheduled.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var retired = events.Retired.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var lost = DeliveryFault.LoseResponse();
        var delayed = DeliveryFault.HoldBeforeCall();
        Deliveries.Enqueue(lost, delayed);
        var started = _cluster.JobClock.GetUtcNow();
        // The source's payload is raw in the sink case and a real application envelope in the handler case.
        if (realSink)
        {
            await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);
        }
        else
        {
            _ = await sender.GetOutboxCountAsync();
            // Use an ordinary outbox envelope to preserve the application's route.
            await Fixture.OnGrainAsync(sender, () => Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<IOutbox>().Send(new OutboxMessage
            {
                ReceiverId = receiver.GetGrainId(),
                Envelope = new() { MessageId = id, Payload = payload, Subject = Route }
            }));
            await sender.WriteAsync();
        }

        Assert.Equal(DeliveryStatus.Accepted, (await lost.ReceiverAnswer).Status);
        Assert.Equal(id, (await retryScheduled).MessageId);
        if (!realSink) await Fixture.WaitForInboxCountAsync((IMessagingTestGrain)receiver, 0);
        await sender.WriteAsync();
        // Start the retry inside its original budget, then delay its arrival beyond receiver compaction.
        // A pending retry could otherwise legitimately expire while the shared clock is two days ahead.
        await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
        await delayed.WaitUntilEnteredAsync();
        Assert.Equal(1, Deliveries.ReceiverCalls);
        _cluster.JobClock.Advance(TimeSpan.FromDays(2));
        await Fixture.RunPumpAsync(sender);
        var pending = await Fixture.OnGrainAsync(sender, () =>
        {
            Assert.True(Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<Outbox>().IsDelivering);
            return _cluster.GetOutboxState(sender).Attempt(id);
        });
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(started + RetryTimeout, pending.ExpirationTime);
        Assert.False(retired.IsCompleted);
        Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        await WriteJournalAsync(receiver);
        var inbox = Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.DoesNotContain(id, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        await _cluster.CrashAsync(receiver);
        _cluster.JobClock.Advance(started - _cluster.JobClock.GetUtcNow());

        var replay = new InboxMessage
        {
            Envelope = new() { MessageId = id, Payload = payload, Subject = realSink ? SourceSinkGrain.Subject : Route },
            RedeliveryDeadline = started + RetryTimeout
        };
        var fenced = await receiver.AsReference<IMessagingExtension>().DeliverAsync(replay, TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryStatus.Expired, fenced.Status);
        Assert.Null(fenced.Code);
        if (realSink)
        {
            Assert.Equal(payload, (await OnSinkAsync(receiver, static grain => grain.GetState())).Received[id.ToString()]);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
        }
        else
        {
            var effect = Assert.Single((await ((IMessagingTestGrain)receiver).GetSnapshotAsync()).Effects);
            Assert.Equal("late-redelivery", effect.Value);
            Assert.Equal(1, effect.Count);
        }

        // Observe this delayed attempt's answer and retirement, not an empty outbox caused by an earlier timeout.
        // Its earlier acceptance is real, but the source must receive UNKNOWN, not a false rejection.
        var answers = Deliveries.WaitForAnswersAsync(2);
        delayed.Release();
        Assert.Equal(DeliveryStatus.Expired, (await delayed.ReceiverAnswer).Status);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Expired],
            await answers.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(OutboxMessageRetirementStatus.DeadLettered, (await retired).Retirement.Status);
        await sender.WriteAsync();
        Assert.Empty(await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).AttemptKeys.ToArray()));
        var terminal = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetter(id));
        Assert.Equal(id, terminal.Message.Envelope.MessageId);
        if (realSink)
        {
            Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered),
                (await OnSinkAsync(sender, static grain => grain.GetState())).Retired[id.ToString()]);
            Assert.Empty(await OnSinkAsync(sender, static grain => grain.GetRetirementCodes()));
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        }
        else
        {
            Assert.Equal(1, Assert.Single((await ((IMessagingTestGrain)receiver).GetSnapshotAsync()).Effects).Count);
        }
    }

    [Fact]
    public async Task SnapshotFloor_CompletionAfterCaptureRemainsUntilItsOwnWriteCommits()
    {
        var receiver = NewGrain();
        const string route = "messages/snapshot-interleaved-completion";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var message = CreateEnvelope(receiver, NewMessage(87, "interleaved"), route) with
        {
            RedeliveryDeadline = _cluster.JobClock.GetUtcNow() + RetryTimeout
        };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await handler.WaitUntilEnteredAsync();
        _cluster.JobClock.Advance(TimeSpan.FromDays(2));
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        Fixture.Storage.RequestSnapshot(journal);
        using var snapshotAcknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        var snapshot = receiver.RetryWriteStateAsync();
        await snapshotAcknowledgement.WaitUntilEnteredAsync();
        using var completionWrite = Fixture.Storage.BlockWrite(journal);
        handler.Release();
        var inbox = Fixture.GetInbox(receiver);
        await Fixture.WaitUntilAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox).Contains(message.Envelope.MessageId));
        snapshotAcknowledgement.Release();
        await snapshot;
        await completionWrite.WaitUntilEnteredAsync();
        Assert.Contains(message.Envelope.MessageId, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        var duplicate = await Fixture.OnGrainAsync(receiver,
            () => inbox.DeliverAsync(message, TestContext.Current.CancellationToken).AsTask());
        Assert.False(duplicate.IsCompleted);
        completionWrite.Release();
        Assert.Equal(DeliveryStatus.Duplicate, (await duplicate).Status);
        await ReactivateAsync(receiver);
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, message)).Status);
        Assert.Equal("interleaved", Assert.Single((await receiver.GetSnapshotAsync()).Effects).Value);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionFloor_ExpiredAnswerWaitsForDurabilityAndRecoversAfterWriteFailure(bool ambiguous)
    {
        var receiver = NewSinkGrain();
        _ = await receiver.GetOutboxCountAsync();
        var previous = Fixture.GetGrainContext(receiver);
        _cluster.JobClock.Advance(TimeSpan.FromDays(2));
        var floor = _cluster.JobClock.GetUtcNow() - TimeSpan.FromDays(1);
        var stale = new InboxMessage
        {
            Envelope = new() { MessageId = NewSourceMessageId("floor-write"), Payload = [88], Subject = SourceSinkGrain.Subject },
            RedeliveryDeadline = floor
        };
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        using var write = ambiguous ? Fixture.Storage.BlockAcknowledgement(journal) : Fixture.Storage.BlockWrite(journal);
        var delivery = receiver.AsReference<IMessagingExtension>().DeliverAsync(stale, TestContext.Current.CancellationToken).AsTask();
        await write.WaitUntilEnteredAsync();
        Assert.False(delivery.IsCompleted);
        Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        write.Fail();
        await Assert.ThrowsAsync<IOException>(() => delivery);
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var inbox = Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(ambiguous ? floor : DateTimeOffset.MinValue,
            await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        Assert.Equal(DeliveryStatus.Expired, (await receiver.AsReference<IMessagingExtension>()
            .DeliverAsync(stale, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(floor, await Fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        Assert.Empty((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
        Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
    }

    public enum RetirementFault { CallbackStaging, BeforeCommit, LostCommitAcknowledgement }
    public enum TerminalOutcome { Delivered, Rejected, Expired }

    [Theory]
    [InlineData(RetirementFault.CallbackStaging, TerminalOutcome.Delivered, false)]
    [InlineData(RetirementFault.CallbackStaging, TerminalOutcome.Delivered, true)]
    [InlineData(RetirementFault.CallbackStaging, TerminalOutcome.Rejected, false)]
    [InlineData(RetirementFault.CallbackStaging, TerminalOutcome.Rejected, true)]
    [InlineData(RetirementFault.CallbackStaging, TerminalOutcome.Expired, false)]
    [InlineData(RetirementFault.CallbackStaging, TerminalOutcome.Expired, true)]
    [InlineData(RetirementFault.BeforeCommit, TerminalOutcome.Delivered, false)]
    [InlineData(RetirementFault.BeforeCommit, TerminalOutcome.Delivered, true)]
    [InlineData(RetirementFault.BeforeCommit, TerminalOutcome.Rejected, false)]
    [InlineData(RetirementFault.BeforeCommit, TerminalOutcome.Rejected, true)]
    [InlineData(RetirementFault.BeforeCommit, TerminalOutcome.Expired, false)]
    [InlineData(RetirementFault.BeforeCommit, TerminalOutcome.Expired, true)]
    [InlineData(RetirementFault.LostCommitAcknowledgement, TerminalOutcome.Delivered, false)]
    [InlineData(RetirementFault.LostCommitAcknowledgement, TerminalOutcome.Delivered, true)]
    [InlineData(RetirementFault.LostCommitAcknowledgement, TerminalOutcome.Rejected, false)]
    [InlineData(RetirementFault.LostCommitAcknowledgement, TerminalOutcome.Rejected, true)]
    [InlineData(RetirementFault.LostCommitAcknowledgement, TerminalOutcome.Expired, false)]
    [InlineData(RetirementFault.LostCommitAcknowledgement, TerminalOutcome.Expired, true)]
    public async Task SourceRetirement_RecoveryCommitsOneOutcomeAndExecutesOneSelfInboxFollowUp(
        RetirementFault fault, TerminalOutcome outcome, bool snapshot)
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        _ = await sender.GetOutboxCountAsync();
        _ = await receiver.GetOutboxCountAsync();
        var started = _cluster.JobClock.GetUtcNow();
        var sourceProbe = _cluster.SourceSinkProbe.For(sender);
        sourceProbe.FollowUpAt = started + TimeSpan.FromHours(2);
        if (outcome == TerminalOutcome.Rejected) _cluster.SourceSinkProbe.For(receiver).Mode = SinkMode.DeadLetter;
        if (outcome == TerminalOutcome.Expired)
        {
            // The receiver has durably fenced this sender's cutoff, then its clock moves backward.
            _cluster.JobClock.Advance(TimeSpan.FromDays(2));
            var stale = new InboxMessage
            {
                Envelope = new() { MessageId = NewSourceMessageId("advance-floor"), Payload = [], Subject = SourceSinkGrain.Subject },
                RedeliveryDeadline = _cluster.JobClock.GetUtcNow() - TimeSpan.FromDays(1)
            };
            Assert.Equal(DeliveryStatus.Expired,
                (await receiver.AsReference<IMessagingExtension>().DeliverAsync(stale, TestContext.Current.CancellationToken)).Status);
            _cluster.JobClock.Advance(started - _cluster.JobClock.GetUtcNow());
        }

        var held = DeliveryFault.HoldAfterCall();
        Deliveries.Enqueue(held);
        var id = NewSourceMessageId("atomic-retirement");
        var payload = RandomNumberGenerator.GetBytes(32);
        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);
        await held.WaitUntilEnteredAsync();
        var answer = await held.ReceiverAnswer;
        Assert.Equal(outcome switch
        {
            TerminalOutcome.Delivered => DeliveryStatus.Accepted,
            TerminalOutcome.Rejected => DeliveryStatus.DeadLettered,
            _ => DeliveryStatus.Expired
        }, answer.Status);
        var journal = JournalId.FromGrainId(sender.GetGrainId());
        var oldContext = Fixture.GetGrainContext(sender);
        if (snapshot) Fixture.Storage.RequestSnapshot(journal);
        switch (fault)
        {
            case RetirementFault.CallbackStaging: sourceProbe.FailNextRetirements(1); break;
            case RetirementFault.BeforeCommit: Fixture.Storage.FailWrite(journal); break;
            case RetirementFault.LostCommitAcknowledgement: Fixture.Storage.FailAfterWrite(journal); break;
        }

        held.Release();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await sender.GetOutboxCountAsync();
        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetTerminalCounts().ContainsKey(id.ToString())
            && !_cluster.GetOutboxState(sender).AttemptKeys.Any());
        await sender.WriteAsync();
        var expectedStatus = outcome switch
        {
            TerminalOutcome.Delivered => OutboxMessageRetirementStatus.Delivered,
            TerminalOutcome.Rejected => OutboxMessageRetirementStatus.Rejected,
            _ => OutboxMessageRetirementStatus.DeadLettered
        };
        var state = await OnSinkAsync(sender, static grain => grain.GetState());
        Assert.Empty(state.Outgoing);
        Assert.Equal(expectedStatus.ToString(), Assert.Single(state.Retired).Value);
        Assert.Equal(new KeyValuePair<string, int>(id.ToString(), 1),
            Assert.Single(await OnSinkAsync(sender, static grain => grain.GetTerminalCounts())));
        Assert.Empty(await OnSinkAsync(sender, static grain => grain.GetFollowUpCounts()));
        var pending = await Fixture.OnGrainAsync(sender, () =>
            InboxTestAccess.GetPendingMessages(Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<Inbox>()).Values.ToArray());
        var work = Assert.Single(pending);
        Assert.Equal(SourceSinkGrain.FollowUpSubject, work.Message.Envelope.Subject);
        Assert.Equal(id.ToString(), System.Text.Encoding.UTF8.GetString(work.Message.Envelope.Payload));
        Assert.Equal(sourceProbe.FollowUpAt, work.Message.Envelope.DeliverAt);
        Assert.Equal(fault == RetirementFault.LostCommitAcknowledgement ? 1 : 2, sourceProbe.RetirementCallbacks);
        var codes = await OnSinkAsync(sender, static grain => grain.GetRetirementCodes());
        if (outcome == TerminalOutcome.Rejected) Assert.Equal(SourceSinkGrain.RejectionCode, codes[id.ToString()]);
        else Assert.Empty(codes);
        if (outcome == TerminalOutcome.Expired)
        {
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            var letter = await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetter(id));
            Assert.Equal(id, letter.Message.Envelope.MessageId);
        }
        else
        {
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(outcome == TerminalOutcome.Delivered ? 1 : 0, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
        }

        // Crash again with durable pending work, then execute it through the recovered real inbox pump.
        await _cluster.CrashAsync(sender);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        await _cluster.AdvanceAndRunPumpAsync(sender, sourceProbe.FollowUpAt!.Value - _cluster.JobClock.GetUtcNow());
        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetFollowUpCounts().Count == 1);
        await _cluster.CrashAsync(sender);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        await Fixture.RunPumpAsync(sender);
        Assert.Equal(new KeyValuePair<string, int>(id.ToString(), 1),
            Assert.Single(await OnSinkAsync(sender, static grain => grain.GetFollowUpCounts())));
        Assert.Equal(1, Assert.Single(await OnSinkAsync(sender, static grain => grain.GetTerminalCounts())).Value);
        Assert.Equal(1, sourceProbe.Handled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFailure_AtomicallyRetiresAndRecoversExecutableSelfInboxFollowUp(bool snapshot)
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var probe = _cluster.SourceSinkProbe.For(sender);
        probe.FollowUpAt = _cluster.JobClock.GetUtcNow() + TimeSpan.FromHours(2);
        var id = NewSourceMessageId("source-error-follow-up");
        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, RandomNumberGenerator.GetBytes(32), write: false);
        probe.Faults[id] = SourceFault.Throw;
        if (snapshot) Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(sender.GetGrainId()));
        await sender.WriteAsync();
        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetTerminalCounts().Count == 1);
        if (snapshot) Fixture.Storage.RequestSnapshot(JournalId.FromGrainId(sender.GetGrainId()));
        await sender.WriteAsync();
        await _cluster.CrashAsync(sender);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        var state = await OnSinkAsync(sender, static grain => grain.GetState());
        Assert.Empty(state.Outgoing);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.DeadLettered), state.Retired[id.ToString()]);
        Assert.Contains(SourceSinkGrain.SourceFailure,
            (await OnSinkAsync(sender, static grain => grain.GetRetirementReasons()))[id.ToString()], StringComparison.Ordinal);
        Assert.Empty(await OnSinkAsync(sender, static grain => grain.GetRetirementCodes()));
        Assert.Equal(1, Assert.Single(await OnSinkAsync(sender, static grain => grain.GetTerminalCounts())).Value);
        Assert.Empty(await OnSinkAsync(sender, static grain => grain.GetFollowUpCounts()));
        await _cluster.AdvanceAndRunPumpAsync(sender, probe.FollowUpAt.Value - _cluster.JobClock.GetUtcNow());
        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetFollowUpCounts().Count == 1);
        Assert.Equal(new KeyValuePair<string, int>(id.ToString(), 1),
            Assert.Single(await OnSinkAsync(sender, static grain => grain.GetFollowUpCounts())));
        Assert.Equal(0, Deliveries.ReceiverCalls);
    }

    private async Task WriteJournalAsync(IAddressable grain)
    {
        var write = await Fixture.OnGrainAsync(grain, () =>
            Fixture.GetGrainContext(grain).ActivationServices.GetRequiredService<IJournaledStateManager>()
                .WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        await write;
    }

    private void ArmSenderWriteFailure(IMessagingTestGrain sender, bool ambiguous)
    {
        var journal = JournalId.FromGrainId(sender.GetGrainId());
        if (ambiguous)
        {
            Fixture.Storage.FailAfterWrite(journal);
        }
        else
        {
            Fixture.Storage.FailWrite(journal);
        }
    }

    private async Task<OutboxMessageState> WaitForFailedAttemptAsync(IAddressable sender, MessageId id, int attempts)
    {
        await _cluster.WaitForFailedAttemptsAsync(sender, id, attempts);
        return await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
    }

    private Task WaitForDrainedOutboxAsync(IMessagingTestGrain sender) =>
        Fixture.WaitUntilAsync(sender, () => Fixture.GetStagedOutput(sender).Length == 0);

    private async Task<EndpointSnapshot> AssertProcessedOnceAsync(IMessagingTestGrain receiver, TestMessage message)
    {
        var processed = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.Effects.Any(effect => effect.LogicalId == message.LogicalId) && snapshot.InboxCount == 0);
        var current = await receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(current.Effects).Count);
        Assert.Equal(message.LogicalId, current.Effects[0].LogicalId);
        Assert.Equal(0, current.InboxCount);
        Assert.Empty(current.InboxDeadLetters);
        return processed;
    }

    private ISourceSinkGrain NewSinkGrain() => Fixture.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());

    private SourceSinkGrain Sink(IAddressable grain) => Assert.IsType<SourceSinkGrain>(Fixture.GetGrainContext(grain).GrainInstance);

    private Task<T> OnSinkAsync<T>(IAddressable grain, Func<SourceSinkGrain, T> func) =>
        Fixture.OnGrainAsync(grain, () => func(Sink(grain)));

    private static MessageId NewSourceMessageId(string name) => MessageId.Create("fault-injection", name, Guid.NewGuid().ToString("N"));

    private static InboxMessage Schedule(InboxMessage message, DateTimeOffset deliverAt) =>
        new() { Envelope = message.Envelope with { DeliverAt = deliverAt } };
}
