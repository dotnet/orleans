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
public sealed class FutureSentAtTests : MessagingBehaviorTestBase
{
    private readonly IndependentJobClockFixture _cluster;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FutureSentAtTests() : this(new IndependentJobClockFixture()) { }

    private FutureSentAtTests(IndependentJobClockFixture cluster) : base(cluster) => _cluster = cluster;

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task UpperBoundary_AllowsEqualityAndRejectsTheNextTickWithoutStaging(long extraTicks, bool accepted)
    {
        var receiver = NewSink();
        var clock = new ManualJobClock(_cluster.JobClock.GetUtcNow());
        _cluster.Clocks.Set(receiver, clock);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var before = await CaptureAsync(receiver);
        var sentAt = clock.GetUtcNow() + Window(receiver) + TimeSpan.FromTicks(extraTicks);
        var message = Message(sentAt);
        if (accepted)
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
            Assert.Equal(before.Writes + 1, _cluster.Storage.GetSuccessfulWriteCount(Journal(receiver)));
            var state = await OnSinkAsync(receiver, static grain => grain.GetState());
            Assert.Equal(message.Envelope.Payload, Assert.Single(state.Received).Value);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
        }
        else
        {
            await AssertRejectedAsync(receiver, message);
            await AssertUnchangedAsync(receiver, before);
            Assert.Empty((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        }
        Assert.Equal(clock.GetUtcNow(), await ActualClockAsync(receiver));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("processed")]
    [InlineData("rejected")]
    public async Task KnownIdentity_RejectsNewerTimePastBoundBeforeRetentionExtension(string history)
    {
        var rejected = history == "rejected";
        IAddressable receiver = rejected ? NewSink() : NewGrain();
        var clock = new ManualJobClock(_cluster.JobClock.GetUtcNow());
        _cluster.Clocks.Set(receiver, clock);
        const string route = "messages/known-future-rejection";
        using var handler = rejected ? null : _cluster.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var original = rejected ? Message(clock.GetUtcNow())
            : TestApplicationProtocol.CreateInbox(Sessions, GrainId.Create("future-test-sender", "known"), route,
                NewMessage(96, "one-known-effect"), clock.GetUtcNow());
        if (rejected) _cluster.SourceSinkProbe.For(receiver).Mode = SinkMode.DeadLetter;
        Assert.Equal(rejected ? DeliveryStatus.DeadLettered : DeliveryStatus.Accepted,
            (await DeliverAsync(receiver, original)).Status);
        if (handler is not null)
        {
            await handler.WaitUntilEnteredAsync();
            if (history == "processed")
            {
                handler.Release();
                await _cluster.WaitForInboxCountAsync((IMessagingTestGrain)receiver, 0);
            }
            await ((IMessagingTestGrain)receiver).RetryWriteStateAsync();
        }
        else
        {
            _cluster.SourceSinkProbe.For(receiver).Mode = SinkMode.Accept;
        }
        var before = await CaptureAsync(receiver);
        var invalid = original with { SentAt = clock.GetUtcNow() + Window(receiver) + TimeSpan.FromTicks(1) };
        await AssertRejectedAsync(receiver, invalid);
        await AssertUnchangedAsync(receiver, before);
        var inbox = Inbox(receiver);
        Assert.Equal(original.SentAt, await _cluster.OnGrainAsync(receiver, () => history == "pending"
            ? InboxTestAccess.GetPendingMessages(inbox)[original.Envelope.MessageId].Message.SentAt
            : InboxTestAccess.GetProcessedSentAt(inbox, original.Envelope.MessageId)));
        var known = await DeliverAsync(receiver, original);
        Assert.Equal(rejected ? DeliveryStatus.DeadLettered : DeliveryStatus.Duplicate, known.Status);
        if (rejected)
        {
            Assert.Equal(SourceSinkGrain.Rejection, known.Message);
            Assert.Equal(SourceSinkGrain.RejectionCode, known.Code);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
        }
        else
        {
            handler!.Release();
            await _cluster.WaitForInboxCountAsync((IMessagingTestGrain)receiver, 0);
            var effect = Assert.Single((await ((IMessagingTestGrain)receiver).GetSnapshotAsync()).Effects);
            Assert.Equal("one-known-effect", effect.Value);
            Assert.Equal(1, effect.Count);
        }
    }

    [Theory]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("processed", false)]
    [InlineData("processed", true)]
    [InlineData("rejected", false)]
    [InlineData("rejected", true)]
    public async Task KnownIdentity_EqualOrEarlierPastBoundKeepsRecordedAnswerWithoutWrites(string history, bool earlier)
    {
        var rejected = history == "rejected";
        IAddressable receiver = rejected ? NewSink() : NewGrain();
        var clock = new ManualJobClock(_cluster.JobClock.GetUtcNow());
        _cluster.Clocks.Set(receiver, clock);
        const string route = "messages/known-answer-after-receiver-rollback";
        using var handler = rejected ? null : _cluster.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var original = rejected ? Message(clock.GetUtcNow())
            : TestApplicationProtocol.CreateInbox(Sessions, GrainId.Create("ordering-test-sender", "known"), route,
                NewMessage(98, "known-despite-rollback"), clock.GetUtcNow());
        if (rejected) _cluster.SourceSinkProbe.For(receiver).Mode = SinkMode.DeadLetter;
        Assert.Equal(rejected ? DeliveryStatus.DeadLettered : DeliveryStatus.Accepted,
            (await DeliverAsync(receiver, original)).Status);
        if (handler is not null)
        {
            await handler.WaitUntilEnteredAsync();
            if (history == "processed")
            {
                handler.Release();
                await _cluster.WaitForInboxCountAsync((IMessagingTestGrain)receiver, 0);
            }
            await ((IMessagingTestGrain)receiver).RetryWriteStateAsync();
        }
        else
        {
            _cluster.SourceSinkProbe.For(receiver).Mode = SinkMode.Accept;
        }
        var window = Window(receiver);
        var before = await CaptureAsync(receiver);
        clock.Advance(-2 * window);
        var receiverTime = await ActualClockAsync(receiver);
        Assert.Equal(original.SentAt - 2 * window, receiverTime);
        var incoming = original with { SentAt = earlier ? original.SentAt - window / 2 : original.SentAt };
        Assert.True(incoming.SentAt > receiverTime + window);
        Assert.True(incoming.SentAt <= original.SentAt);
        var known = await DeliverAsync(receiver, incoming);
        Assert.Equal(rejected ? DeliveryStatus.DeadLettered : DeliveryStatus.Duplicate, known.Status);
        Assert.Equal(rejected ? SourceSinkGrain.RejectionCode : null, known.Code);
        if (rejected) Assert.Equal(SourceSinkGrain.Rejection, known.Message);
        await AssertUnchangedAsync(receiver, before);
        var inbox = Inbox(receiver);
        Assert.Equal(original.SentAt, await _cluster.OnGrainAsync(receiver, () => history == "pending"
            ? InboxTestAccess.GetPendingMessages(inbox)[original.Envelope.MessageId].Message.SentAt
            : InboxTestAccess.GetProcessedSentAt(inbox, original.Envelope.MessageId)));
        Assert.Equal(receiverTime, await ActualClockAsync(receiver));
        if (rejected)
        {
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
            Assert.Empty((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
        }
        else
        {
            handler!.Release();
            await _cluster.WaitForInboxCountAsync((IMessagingTestGrain)receiver, 0);
            var effect = Assert.Single((await ((IMessagingTestGrain)receiver).GetSnapshotAsync()).Effects);
            Assert.Equal("known-despite-rollback", effect.Value);
            Assert.Equal(1, effect.Count);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task UnknownPastBound_AtOrBelowDurableFloorExpiresBeforeFutureRejection(long floorTicks)
    {
        var receiver = NewSink();
        var started = _cluster.JobClock.GetUtcNow();
        var clock = new ManualJobClock(started);
        _cluster.Clocks.Set(receiver, clock);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var window = Window(receiver);
        clock.Advance(3 * window);
        var floor = clock.GetUtcNow() - window;
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, Message(floor))).Status);
        Assert.Equal(floor, await _cluster.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(Inbox(receiver))));
        clock.Advance(started - clock.GetUtcNow());
        var receiverTime = await ActualClockAsync(receiver);
        Assert.Equal(started, receiverTime);
        Assert.True(floor > receiverTime + window);
        var before = await CaptureAsync(receiver);
        var incoming = Message(floor.AddTicks(floorTicks));
        Assert.True(incoming.SentAt > receiverTime + window);
        Assert.True(incoming.SentAt <= floor);
        var expired = await DeliverAsync(receiver, incoming);
        Assert.Equal(DeliveryStatus.Expired, expired.Status);
        Assert.Null(expired.Code);
        await AssertUnchangedAsync(receiver, before);
        Assert.Empty((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
        Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        await AssertRejectedAsync(receiver, Message(floor.AddTicks(1)));
        await AssertUnchangedAsync(receiver, before);
        Assert.Equal(receiverTime, await ActualClockAsync(receiver));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaxValue_RejectsAtNormalTimeAndAcceptsClampedUpperBoundEquality(bool nearMaximum)
    {
        var receiver = NewSink();
        var now = nearMaximum
            ? DateTimeOffset.MaxValue - TimeSpan.FromHours(12)
            : _cluster.JobClock.GetUtcNow();
        var clock = new ManualJobClock(now);
        _cluster.Clocks.Set(receiver, clock);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var before = await CaptureAsync(receiver);
        var message = Message(DateTimeOffset.MaxValue);
        if (nearMaximum)
        {
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
            Assert.Equal(DateTimeOffset.MaxValue, await _cluster.OnGrainAsync(receiver, () =>
                InboxTestAccess.GetProcessedSentAt(Inbox(receiver), message.Envelope.MessageId)));
            Assert.Equal(message.Envelope.Payload, Assert.Single((await OnSinkAsync(receiver, static grain => grain.GetState())).Received).Value);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        }
        else
        {
            await AssertRejectedAsync(receiver, message);
            await AssertUnchangedAsync(receiver, before);
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
        }
        Assert.Equal(now, await ActualClockAsync(receiver));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealOutbox_IndependentClockSkewDeliversWithinWindowOrRetriesGuardUntilOriginalDeadline(bool tooFar)
    {
        var sender = NewSink();
        var receiver = NewSink();
        var receiverTime = _cluster.JobClock.GetUtcNow();
        var receiverClock = new ManualJobClock(receiverTime);
        var senderClock = new ManualJobClock(receiverTime + (tooFar ? TimeSpan.FromDays(2) : TimeSpan.FromHours(12)));
        _cluster.Clocks.Set(sender, senderClock);
        _cluster.Clocks.Set(receiver, receiverClock);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var before = await CaptureAsync(receiver);
        var sentAt = senderClock.GetUtcNow();
        Assert.Equal(tooFar, sentAt > receiverTime + Window(receiver));
        await AssertClocksAsync();
        var id = TestApplicationProtocol.NewMessageId();
        byte[] payload = [97, 98, 99];
        using var events = new DeliveryEventObserver(sender.GetGrainId(), receiver.GetGrainId(), id, faults: 0);
        using var diagnostics = new DiagnosticEventCollector(MessagingEvents.ListenerName);
        var retired = events.Retired.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var firstRetry = tooFar ? WaitForRetryAsync(1) : null;
        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);
        await _cluster.RunPumpAsync(sender);
        if (!tooFar)
        {
            Assert.Equal(OutboxMessageRetirementStatus.Delivered, (await retired).Retirement.Status);
            await sender.WriteAsync();
            Assert.Equal([DeliveryStatus.Accepted], _cluster.Deliveries.Answers);
            Assert.Equal([sentAt], _cluster.Deliveries.SentTimes);
            Assert.Empty(_cluster.Deliveries.ReceiverFailures);
            Assert.Empty(events.Retries);
            Assert.Equal(1, _cluster.Deliveries.Executions);
            Assert.Equal(1, _cluster.Deliveries.ReceiverCalls);
            Assert.Equal(payload, (await OnSinkAsync(receiver, static grain => grain.GetState())).Received[id.ToString()]);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
        }
        else
        {
            var deadline = sentAt + FaultInjectionClusterFixture.OutboxRetryTimeout;
            OutboxMessageState? state = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var retry = attempt == 1 ? firstRetry! : WaitForRetryAsync(attempt);
                if (attempt > 1)
                {
                    senderClock.Advance(state!.NextAttemptAt!.Value - senderClock.GetUtcNow());
                    await _cluster.RunPumpAsync(sender);
                }
                await retry;
                await sender.WriteAsync();
                state = await _cluster.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
                Assert.Equal(attempt, state.AttemptCount);
                Assert.Equal(sentAt, state.SentAt);
                var next = sentAt + FaultInjectionClusterFixture.RetryInterval * ((1 << attempt) - 1);
                Assert.Equal(next < deadline ? next : deadline, state.NextAttemptAt);
                Assert.Contains(nameof(ArgumentOutOfRangeException), state.LastError, StringComparison.Ordinal);
                Assert.Equal(attempt, _cluster.Deliveries.ReceiverCalls);
                Assert.Equal(attempt, _cluster.Deliveries.ReceiverFailures.Count);
                Assert.All(_cluster.Deliveries.ReceiverFailures, error => AssertFutureException(Assert.IsType<ArgumentOutOfRangeException>(error), sentAt));
                Assert.All(_cluster.Deliveries.SentTimes, value => Assert.Equal(sentAt, value));
                Assert.Empty(_cluster.Deliveries.Answers);
                Assert.False(retired.IsCompleted);
                await AssertUnchangedAsync(receiver, before);
                await AssertClocksAsync();
            }
            senderClock.Advance(deadline - senderClock.GetUtcNow());
            Assert.Equal(deadline, senderClock.GetUtcNow());
            await _cluster.RunPumpAsync(sender);
            var terminal = (await retired).Retirement;
            Assert.Equal(OutboxMessageRetirementStatus.DeadLettered, terminal.Status);
            Assert.Null(terminal.Code);
            await sender.WriteAsync();
            Assert.Empty(await OnSinkAsync(sender, static grain => grain.GetRetirementCodes()));
            var letter = await _cluster.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetter(id));
            Assert.Contains("retry timeout elapsed", letter.Reason, StringComparison.Ordinal);
            Assert.Equal(3, letter.AttemptCount);
            Assert.Equal(3, _cluster.Deliveries.Executions);
            Assert.Equal(3, _cluster.Deliveries.ReceiverCalls);
            Assert.Equal(3, events.Retries.Count);
            await AssertUnchangedAsync(receiver, before);
            Assert.Empty((await OnSinkAsync(receiver, static grain => grain.GetState())).Received);
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(0, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
            await AssertClocksAsync();
        }
        var final = await OnSinkAsync(sender, static grain => grain.GetState());
        Assert.Empty(final.Outgoing);
        Assert.Equal(tooFar ? nameof(OutboxMessageRetirementStatus.DeadLettered) : nameof(OutboxMessageRetirementStatus.Delivered),
            Assert.Single(final.Retired).Value);
        Assert.Equal(0, await sender.GetOutboxCountAsync());

        Task<DiagnosticEvent> WaitForRetryAsync(int attempt) => diagnostics.WaitForEventAsync(
            nameof(MessagingEvents.OutboxRetryScheduled),
            entry => entry.Payload is MessagingEvents.OutboxRetryScheduled retry
                && retry.GrainContext.GrainId == sender.GetGrainId() && retry.MessageId == id && retry.AttemptCount == attempt,
            TimeSpan.FromSeconds(30), Token);

        async Task AssertClocksAsync()
        {
            Assert.Equal(senderClock.GetUtcNow(), await ActualClockAsync(sender));
            Assert.Equal(receiverTime, await ActualClockAsync(receiver));
            Assert.Equal(receiverTime, receiverClock.GetUtcNow());
            Assert.Equal(receiverTime, _cluster.JobClock.GetUtcNow());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealOutbox_LostReplyThenReceiverRollbackReturnsDuplicateAndRetiresDelivered(bool realSink)
    {
        var sender = NewSink();
        IAddressable receiver = realSink ? NewSink() : NewGrain();
        var started = _cluster.JobClock.GetUtcNow();
        var senderClock = new ManualJobClock(started);
        var receiverClock = new ManualJobClock(started);
        _cluster.Clocks.Set(sender, senderClock);
        _cluster.Clocks.Set(receiver, receiverClock);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        if (realSink) Assert.Equal(0, await ((ISourceSinkGrain)receiver).GetOutboxCountAsync());
        else _ = await ((IMessagingTestGrain)receiver).GetSnapshotAsync();
        var id = TestApplicationProtocol.NewMessageId();
        const string route = "messages/lost-reply-receiver-rollback";
        var payload = realSink ? new byte[] { 100, 101 }
            : TestApplicationProtocol.Encode(Sessions, new TestApplicationMessage(route, new TestMessage(id, 100, "once-after-receiver-rollback")));
        if (!realSink) _cluster.SourceSinkProbe.For(sender).SubjectOverride = route;
        using var events = new DeliveryEventObserver(sender.GetGrainId(), receiver.GetGrainId(), id, faults: 0);
        var retry = events.RetryScheduled.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var retired = events.Retired.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var lost = DeliveryFault.LoseResponse();
        var held = DeliveryFault.HoldAfterCall();
        var retryAnswer = held.ReceiverAnswer;
        _cluster.Deliveries.Enqueue(lost, held);
        await sender.StageFromSourceAsync(receiver.GetGrainId(), id, payload, write: true);
        await _cluster.RunPumpAsync(sender);
        Assert.Equal(DeliveryStatus.Accepted, (await lost.ReceiverAnswer).Status);
        await retry;
        await sender.WriteAsync();
        if (!realSink)
        {
            await _cluster.WaitForEffectCountAsync((IMessagingTestGrain)receiver, 1);
            await _cluster.WaitForInboxCountAsync((IMessagingTestGrain)receiver, 0);
            await ((IMessagingTestGrain)receiver).RetryWriteStateAsync();
        }
        var before = await CaptureAsync(receiver);
        var window = Window(receiver);
        receiverClock.Advance(-2 * window - TimeSpan.FromHours(1));
        Assert.True(started > receiverClock.GetUtcNow() + window);
        var state = await _cluster.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).Attempt(id));
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(started, state.SentAt);
        senderClock.Advance(state.NextAttemptAt!.Value - senderClock.GetUtcNow());
        await _cluster.RunPumpAsync(sender);
        await held.WaitUntilEnteredAsync();
        Assert.Equal(DeliveryStatus.Duplicate, (await retryAnswer).Status);
        Assert.Equal([DeliveryStatus.Accepted, DeliveryStatus.Duplicate], _cluster.Deliveries.Answers);
        Assert.Equal([started, started], _cluster.Deliveries.SentTimes);
        Assert.Empty(_cluster.Deliveries.ReceiverFailures);
        Assert.Single(events.Retries);
        Assert.False(retired.IsCompleted);
        await AssertUnchangedAsync(receiver, before);
        Assert.Equal(started, await _cluster.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedSentAt(Inbox(receiver), id)));
        held.Release();
        Assert.Equal(OutboxMessageRetirementStatus.Delivered, (await retired).Retirement.Status);
        await sender.WriteAsync();
        var final = await OnSinkAsync(sender, static grain => grain.GetState());
        Assert.Empty(final.Outgoing);
        Assert.Equal(nameof(OutboxMessageRetirementStatus.Delivered), Assert.Single(final.Retired).Value);
        Assert.Equal(0, await sender.GetOutboxCountAsync());
        Assert.Empty(await _cluster.OnGrainAsync(sender, () => _cluster.GetOutboxState(sender).DeadLetterKeys.ToArray()));
        Assert.Equal(2, _cluster.Deliveries.Executions);
        Assert.Equal(2, _cluster.Deliveries.ReceiverCalls);
        Assert.Single(events.Retries);
        Assert.Equal(started + FaultInjectionClusterFixture.RetryInterval, await ActualClockAsync(sender));
        Assert.Equal(started - 2 * window - TimeSpan.FromHours(1), await ActualClockAsync(receiver));
        Assert.Equal(started, _cluster.JobClock.GetUtcNow());
        if (realSink)
        {
            Assert.Equal(payload, (await OnSinkAsync(receiver, static grain => grain.GetState())).Received[id.ToString()]);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkOffers);
            Assert.Equal(1, _cluster.SourceSinkProbe.For(receiver).SinkAcceptances);
        }
        else
        {
            var effect = Assert.Single((await ((IMessagingTestGrain)receiver).GetSnapshotAsync()).Effects);
            Assert.Equal("once-after-receiver-rollback", effect.Value);
            Assert.Equal(1, effect.Count);
        }
    }

    private ISourceSinkGrain NewSink() => _cluster.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());
    private Inbox Inbox(IAddressable receiver) => _cluster.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
    private static JournalId Journal(IAddressable receiver) => JournalId.FromGrainId(receiver.GetGrainId());
    private TimeSpan Window(IAddressable receiver) => _cluster.GetGrainContext(receiver).ActivationServices
        .GetRequiredService<IOptions<DurableMessagingOptions>>().Value.DeduplicationWindow;
    private Task<T> OnSinkAsync<T>(IAddressable grain, Func<SourceSinkGrain, T> action) =>
        _cluster.OnGrainAsync(grain, () => action(Assert.IsType<SourceSinkGrain>(_cluster.GetGrainContext(grain).GrainInstance)));
    private Task<DateTimeOffset> ActualClockAsync(IAddressable grain) => _cluster.OnGrainAsync(grain, () =>
        _cluster.GetGrainContext(grain).ActivationServices.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs).GetUtcNow());
    private static async Task<DeliveryResult> DeliverAsync(IAddressable receiver, InboxMessage message) =>
        await receiver.AsReference<IMessagingExtension>().DeliverAsync(message, Token);
    private static InboxMessage Message(DateTimeOffset sentAt) => new()
    {
        Envelope = new() { MessageId = TestApplicationProtocol.NewMessageId(), Payload = [1, 2, 3], Subject = SourceSinkGrain.Subject },
        SentAt = sentAt
    };

    private static void AssertFutureException(ArgumentOutOfRangeException error, DateTimeOffset sentAt)
    {
        Assert.Equal("message", error.ParamName);
        Assert.Equal(sentAt, Assert.IsType<DateTimeOffset>(error.ActualValue));
        Assert.Contains("SentAt must not be later than the receiver's clock plus DeduplicationWindow.", error.Message, StringComparison.Ordinal);
    }

    private async Task AssertRejectedAsync(IAddressable receiver, InboxMessage message)
    {
        // Inspect the actual guard exception before external-client exception serialization normalizes its metadata.
        var direct = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            var delivery = await _cluster.OnGrainAsync(receiver, () => Inbox(receiver).DeliverAsync(message, Token).AsTask());
            await delivery;
        });
        AssertFutureException(direct, message.SentAt);
        var remote = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DeliverAsync(receiver, message));
        Assert.Contains("SentAt must not be later than the receiver's clock plus DeduplicationWindow.", remote.Message, StringComparison.Ordinal);
    }

    private Task<ReceiverState> CaptureAsync(IAddressable receiver) => _cluster.OnGrainAsync(receiver, () =>
    {
        var inbox = Inbox(receiver);
        return new ReceiverState(_cluster.Storage.GetSuccessfulWriteCount(Journal(receiver)),
            _cluster.GetGrainContext(receiver).ActivationServices.GetRequiredService<IJournaledStateManager>().PendingWriteByteCount,
            inbox.Count, InboxTestAccess.GetProcessedKeys(inbox), InboxTestAccess.AdmissionFloor(inbox),
            InboxTestAccess.DurableAdmissionFloor(inbox), _cluster.SourceSinkProbe.For(receiver).SinkOffers);
    });

    private async Task AssertUnchangedAsync(IAddressable receiver, ReceiverState expected)
    {
        var actual = await CaptureAsync(receiver);
        Assert.Equal(expected.Writes, actual.Writes);
        Assert.Equal(expected.StagedBytes, actual.StagedBytes);
        Assert.Equal(expected.PendingCount, actual.PendingCount);
        Assert.True(expected.Processed.SetEquals(actual.Processed));
        Assert.Equal(expected.Floor, actual.Floor);
        Assert.Equal(expected.DurableFloor, actual.DurableFloor);
        Assert.Equal(expected.SinkOffers, actual.SinkOffers);
    }

    private sealed record ReceiverState(int Writes, long StagedBytes, int PendingCount, HashSet<MessageId> Processed,
        DateTimeOffset Floor, DateTimeOffset DurableFloor, int SinkOffers);
}
