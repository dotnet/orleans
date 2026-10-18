using System.Security.Cryptography;
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

    [Theory]
    [InlineData(DeliveryFaultKind.Throw, "Injected transient delivery failure.")]
    [InlineData(DeliveryFaultKind.TimeOut, "TimeoutRejectedException")]
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
    public async Task SinkBackpressure_ThroughTheOutbox_RetriesBeyondTheRetryTimeoutUntilTheSinkAccepts()
    {
        var sender = NewSinkGrain();
        var receiver = NewSinkGrain();
        var probe = _cluster.SourceSinkProbe.For(receiver);
        probe.Mode = SinkMode.Backpressure;
        var id = NewSourceMessageId("backpressure");
        var payload = RandomNumberGenerator.GetBytes(32);

        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);

        var first = await WaitForFailedAttemptAsync(sender, id, 1);
        Assert.Null(first.ExpirationTime);
        Assert.Equal("The receiver is backpressured.", first.LastError);

        // Backpressure proves the receiver took no earlier attempt, so it never starts the retry timeout.
        await _cluster.AdvanceAndRunPumpAsync(sender, 2 * RetryTimeout);
        var second = await WaitForFailedAttemptAsync(sender, id, 2);
        Assert.Null(second.ExpirationTime);
        Assert.Empty(await Fixture.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetterKeys.ToArray()));

        probe.Mode = SinkMode.Accept;
        await _cluster.AdvanceAndRunPumpAsync(sender, 2 * RetryInterval);

        await Fixture.WaitUntilAsync(sender, () => Sink(sender).GetState().Retired.Count == 1);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), (await OnSinkAsync(sender, static grain => grain.GetState())).Retired[id.ToString()]);
        Assert.Equal([DeliveryStatus.Backpressured, DeliveryStatus.Backpressured, DeliveryStatus.Accepted], Deliveries.Answers);
        Assert.Equal(3, probe.SinkOffers);
        Assert.Equal(1, probe.SinkAcceptances);
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
    public async Task CanceledPumpRun_WhoseWakeupRepairFails_RecoversAfterReactivation()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute);
        var message = NewMessage(10, "canceled-run");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, CreateEnvelope(receiver, message, HeldRoute))).Status);
        await WaitForBarrierAsync(receiver, barrier);
        var previous = Fixture.GetGrainContext(receiver);

        // Durable Jobs cancels a run while the handler is still working, and re-establishing the wake-up fails.
        Fixture.JobManagerProbe.FailNext(MessagingPump.JobName);
        using var cancellation = new CancellationTokenSource();
        var run = Fixture.RunCancelablePumpAsync(receiver, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Without a wake-up the activation can't guarantee progress, so it deactivates and recovery takes over.
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        barrier.Dispose();
        _ = await receiver.GetSnapshotAsync();

        var processed = await AssertProcessedOnceAsync(receiver, message);
        Assert.NotEqual(before.ActivationId, processed.ActivationId);
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
