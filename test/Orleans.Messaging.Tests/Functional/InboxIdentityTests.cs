using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxIdentityTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task PendingCommand_NewSenderCoalescesAndPreservesOriginalEnvelope()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = MessageId.Create("tenant", "orders", "1042", "reserve");
        var original = Create(rig, key, "inventory.reserve.v1", "original");
        var repeated = TestApplicationProtocol.With(original, Sessions, sender: GrainId.Create("forwarder", "second"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        var writes = Writes(rig);
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId);
        byte[] storedPayload = null!;
        await OnTurnAsync(rig.Context, () => storedPayload = Assert.Single(rig.Pending).Value.Message.Envelope.Payload);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverOnTurnAsync(rig, repeated)).Status);
        await OnTurnAsync(rig.Context, () =>
        {
            Assert.True(rig.Inbox.MessageState.TryGetValue(key, out var entry));
            var stored = entry.Message;
            Assert.Equal(TestApplicationProtocol.Sender(Sessions, original.Envelope), TestApplicationProtocol.Sender(Sessions, stored.Envelope));
            Assert.Equal(TestApplicationProtocol.Subject(original.Envelope), TestApplicationProtocol.Subject(stored.Envelope));
            Assert.Same(storedPayload, stored.Envelope.Payload);
            Assert.Equal(original.Envelope.Payload.ToArray(), stored.Envelope.Payload.ToArray());
            Assert.Single(rig.Pending);
            Assert.Empty(rig.Processed);
        });
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId));
        handler.Release.TrySetResult();
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(key, Assert.Single(rig.Grain.GetSnapshotForTest().Effects).LogicalId);
        Assert.Equal(1, Assert.Single(rig.Grain.GetSnapshotForTest().Effects).Count);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("subject-case")]
    [InlineData("body")]
    public async Task PendingCommand_ConflictingContentRejectsBeforeSchedulingOrMutation(string difference)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = MessageId.Create("tenant", "orders", "1043", "reserve");
        var original = Create(rig, key, "inventory.reserve.v1", "original");
        var conflictingSubject = difference switch
        {
            "subject" => "inventory.release.v1",
            "subject-case" => "Inventory.reserve.v1",
            _ => Assert.IsType<string>(TestApplicationProtocol.Subject(original.Envelope))
        };
        var replacement = Create(rig, key, conflictingSubject, "different-parameters",
            GrainId.Create("forwarder", "other"));
        var conflict = difference == "body" ? replacement : TestApplicationProtocol.With(original, Sessions,
            sender: TestApplicationProtocol.Sender(Sessions, replacement.Envelope), subject: conflictingSubject);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        var writes = Writes(rig);
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId);
        byte[] storedPayload = null!;
        await OnTurnAsync(rig.Context, () => storedPayload = Assert.Single(rig.Pending).Value.Message.Envelope.Payload);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverOnTurnAsync(rig, conflict));
        Assert.Contains(key.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains("different command", failure.Message, StringComparison.Ordinal);
        await OnTurnAsync(rig.Context, () =>
        {
            Assert.Equal(TestApplicationProtocol.Sender(Sessions, original.Envelope), TestApplicationProtocol.Sender(Sessions, Assert.Single(rig.Pending).Value.Message.Envelope));
            Assert.Equal(TestApplicationProtocol.Subject(original.Envelope), TestApplicationProtocol.Subject(Assert.Single(rig.Pending).Value.Message.Envelope));
            Assert.Same(storedPayload, Assert.Single(rig.Pending).Value.Message.Envelope.Payload);
            Assert.Equal(original.Envelope.Payload.ToArray(), Assert.Single(rig.Pending).Value.Message.Envelope.Payload.ToArray());
            Assert.Empty(rig.Processed);
            Assert.Empty(rig.Grain.GetSnapshotForTest().Effects);
            Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        });
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId));
        await OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "healthy";
            await rig.Manager.WriteStateAsync(CancellationToken.None);
        });
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverOnTurnAsync(rig, original)).Status);
        handler.Release.TrySetResult();
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, Assert.Single(rig.Grain.GetSnapshotForTest().Effects).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedCommand_ChangedSenderSubjectAndBodyAcknowledgesWithoutFingerprint(bool restart)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = MessageId.Create("tenant", "orders", "1044", "reserve");
        var original = Create(rig, key, "inventory.reserve.v1", "original");
        var repeated = Create(rig, key, "completely.changed.subject.v2", "different-body",
            GrainId.Create("forwarder", "new-owner"));
        handler.Release.TrySetResult();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        await Fixture.WaitForInboxCountAsync(rig.Receiver, 0);
        var prior = rig.Context;
        if (restart)
        {
            await rig.Receiver.RequestDeactivationAsync();
            await WaitAsync(prior.Deactivated);
            await rig.Receiver.GetSnapshotAsync();
            Assert.NotSame(prior, Fixture.GetGrainContext(rig.Receiver));
        }
        var writes = Writes(rig);
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, repeated)).Status);
        var completed = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId));
    }

    [Fact]
    public async Task ExactParentChildAndSiblingCommandsHaveIndependentCompletions()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Release.TrySetResult();
        var parent = MessageId.Create("tenant", "orders", "1045");
        var keys = new[] { parent, new MessageId(parent.Key.CreateChildKey("reserve")), new MessageId(parent.Key.CreateChildKey("release")) };
        for (var index = 0; index < keys.Length; index++)
        {
            var command = Create(rig, keys[index], "inventory.command.v1", $"command-{index}");
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, command)).Status);
            await Fixture.WaitForEffectCountAsync(rig.Receiver, index + 1);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, command)).Status);
        }
        var completed = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(3, completed.Effects.Count);
        Assert.Equal(3, completed.ProcessedMessageCount);
        Assert.All(completed.Effects, effect => Assert.Equal(1, effect.Count));
        Assert.Equal(keys.OrderBy(static key => key.ToString(), StringComparer.Ordinal),
            completed.Effects.Select(static effect => effect.LogicalId).OrderBy(static key => key.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public async Task SameCommandKey_InDifferentReceiversCompletesIndependently()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        using var firstHandler = first.Handler;
        using var secondHandler = second.Handler;
        firstHandler.Release.TrySetResult();
        secondHandler.Release.TrySetResult();
        var key = MessageId.Create("tenant", "orders", "1049", "reserve");
        var one = Create(first, key, "inventory.reserve.v1", "first-receiver");
        var two = Create(second, key, "inventory.reserve.v1", "second-receiver");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(first.Receiver, one)).Status);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(second.Receiver, two)).Status);
        var firstOutcome = await Fixture.WaitForEffectCountAsync(first.Receiver, 1);
        var secondOutcome = await Fixture.WaitForEffectCountAsync(second.Receiver, 1);
        Assert.Equal(key, Assert.Single(firstOutcome.Effects).LogicalId);
        Assert.Equal(key, Assert.Single(secondOutcome.Effects).LogicalId);
        Assert.Equal(1, firstOutcome.ProcessedMessageCount);
        Assert.Equal(1, secondOutcome.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(first.Receiver, one)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(second.Receiver, two)).Status);
        Assert.Equal(1, firstHandler.Calls);
        Assert.Equal(1, secondHandler.Calls);
    }

    [Fact]
    public async Task PendingIdentityAfterReplayStillRejectsConflictAndDeduplicatesNewSender()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = MessageId.Create("tenant", "orders", "1046", "reserve");
        var original = Create(rig, key, "inventory.reserve.v1", "original");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        await OnTurnAsync(rig.Context, () =>
        {
            rig.Context.Deactivate(new DeactivationReason(
                DeactivationReasonCode.ApplicationRequested, "Verify pending command identity replay."),
                TestContext.Current.CancellationToken);
        });
        await WaitAsync(rig.Context.Deactivated);
        using var recoveredHandler = new IdentityHandler();
        using var read = Fixture.Storage.BlockRead(JournalId.FromGrainId(rig.Context.GrainId));
        var activation = rig.Receiver.GetSnapshotAsync();
        await read.WaitUntilEnteredAsync();
        var recoveredContext = Fixture.GetGrainContext(rig.Receiver);
        var recoveredGrain = Assert.IsType<MessagingTestGrain>(recoveredContext.GrainInstance);
        await OnTurnAsync(recoveredContext, () => recoveredGrain.HandlerOverride = recoveredHandler);
        read.Release();
        await WaitAsync(recoveredHandler.Entered.Task);
        var recovered = CreateRig(rig.Receiver, recoveredContext, recoveredHandler);
        Assert.NotSame(rig.Context, recovered.Context);
        var repeated = TestApplicationProtocol.With(original, Sessions,
            sender: GrainId.Create("forwarder", "after-replay"));
        var conflict = Create(recovered, key, "inventory.reserve.v1", "changed",
            GrainId.Create("forwarder", "after-replay"));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverOnTurnAsync(recovered, repeated)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverOnTurnAsync(recovered, conflict));
        Assert.Equal(TestApplicationProtocol.Sender(Sessions, original.Envelope), TestApplicationProtocol.Sender(Sessions, Assert.Single(recovered.Pending).Value.Message.Envelope));
        Assert.Empty(recovered.Processed);
        Assert.False(recovered.Grain.DeactivationFailure.Task.IsCompleted);
        recoveredHandler.Release.TrySetResult();
        await activation;
        var completed = await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
    }

    [Fact]
    public async Task DeadLetterInspectionAndRemovalUseReceiverLocalKeyAcrossReplay()
    {
        var receiver = NewGrain();
        var key = MessageId.Create("tenant", "orders", "1048", "invalid-command");
        var body = new TestMessage(key, 611, "poison", ThrowDuringPreparation: true);
        var original = CreateEnvelope(receiver, body);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, original)).Status);
        var deadLetter = Assert.Single((await Fixture.WaitForDeadLetterCountAsync(receiver, 1)).InboxDeadLetters);
        Assert.Equal(key, deadLetter.MessageId);
        var old = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await WaitAsync(old.Deactivated);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.Equal(key, Assert.Single(recovered.InboxDeadLetters).MessageId);
        var duplicate = TestApplicationProtocol.With(original, Sessions,
            sender: GrainId.Create("forwarder", "deadletter"), subject: "another.subject.v1");
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, duplicate)).Status);
        Assert.True(await receiver.RemoveInboxDeadLetterAsync(key));
        Assert.False(await receiver.RemoveInboxDeadLetterAsync(key));
        var current = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await WaitAsync(current.Deactivated);
        Assert.Empty((await receiver.GetSnapshotAsync()).InboxDeadLetters);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, duplicate)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOptionalMetadata_RawHandlerCompletesWithoutSubject(bool removeSender)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = MessageId.Create("tenant", "invalid-subject", "command");
        var envelope = Create(rig, key, "valid.subject.v1", "missing-subject");
        var raw = TestApplicationProtocol.With(envelope, Sessions, removeSubject: true, removeSender: removeSender);
        var bytes = envelope.Envelope.Payload.ToArray();
        handler.Release.TrySetResult();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverOnTurnAsync(rig, raw)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Null(raw.Envelope.Subject);
        if (removeSender) Assert.DoesNotContain("sender", Assert.IsType<Dictionary<string, byte[]>>(raw.Envelope.Headers));
        Assert.Equal(bytes, raw.Envelope.Payload.ToArray());
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverOnTurnAsync(rig, raw)).Status);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
    }

    [Theory]
    [InlineData("key-bytes")]
    [InlineData("key-utf8")]
    [InlineData("subject-bytes")]
    [InlineData("subject-utf8")]
    public async Task OversizedMetadata_RejectsBeforeSchedulingOrPersistence(string field)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = field switch
        {
            "key-bytes" => MessageId.Create(new string('x', 1025)),
            "key-utf8" => MessageId.Create(new string('\u00e9', 513)),
            _ => MessageId.Create("tenant", "bounded", "command")
        };
        var subject = field switch
        {
            "subject-bytes" => new string('x', 257),
            "subject-utf8" => new string('\u00e9', 129),
            _ => "inventory.reserve.v1"
        };
        var writes = Writes(rig);
        var message = Create(rig, key, subject, "oversized");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => DeliverOnTurnAsync(rig, message));
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, rig.Context.GrainId));
        Assert.Empty(rig.Pending);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Grain.GetSnapshotForTest().Effects);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        Assert.False(handler.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData("key-bytes")]
    [InlineData("key-utf8")]
    [InlineData("subject-bytes")]
    [InlineData("subject-utf8")]
    [InlineData("subject-space")]
    public async Task MetadataAtExactDefaultLimit_IsAccepted(string field)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Release.TrySetResult();
        var key = field switch
        {
            "key-bytes" => MessageId.Create(new string('x', 1024)),
            "key-utf8" => MessageId.Create(new string('\u00e9', 512)),
            "key-depth" => MessageId.Create(Enumerable.Repeat("segment", 32).ToArray()),
            _ => MessageId.Create("tenant", "bounded", "command")
        };
        var subject = field switch
        {
            "subject-bytes" => new string('x', 256),
            "subject-utf8" => new string('\u00e9', 128),
            "subject-space" => " ",
            _ => "inventory.reserve.v1"
        };
        var envelope = Create(rig, key, subject, "maximum");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, envelope)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, envelope)).Status);
    }

    [Fact]
    public async Task ExpiredIdentityCanAcceptChangedSenderSubjectAndBodyAtExactBoundary()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Release.TrySetResult();
        var key = MessageId.Create("tenant", "orders", "1047", "reserve");
        var original = Create(rig, key, "inventory.reserve.v1", "original");
        var repeated = Create(rig, key, "inventory.reserve.v2", "changed",
            GrainId.Create("forwarder", "new"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        await Fixture.WaitForInboxCountAsync(rig.Receiver, 0);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, repeated)).Status);
        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, repeated)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(rig.Receiver, 2);
        Assert.Equal(2, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(2, handler.Calls);
    }

    private async Task<Rig> CreateAsync()
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var handler = new IdentityHandler();
        var rig = CreateRig(receiver, context, handler);
        await OnTurnAsync(context, () => rig.Grain.HandlerOverride = handler);
        return rig;
    }

    private static Rig CreateRig(IMessagingTestGrain receiver, IGrainContext context, IdentityHandler handler) =>
        new(receiver, context, Assert.IsType<MessagingTestGrain>(context.GrainInstance), handler,
            context.ActivationServices.GetRequiredService<IJournaledStateManager>(),
            context.ActivationServices.GetRequiredService<Inbox>());

    private InboxMessage Create(Rig rig, MessageId key, string subject, string value, GrainId sender = default) =>
        TestApplicationProtocol.CreateInbox(Sessions,
            sender.IsDefault ? GrainId.Create("producer", "first") : sender,
            subject, new TestMessage(key, 610, value), key);

    private int Writes(Rig rig) => Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(rig.Context.GrainId));

    private static Task<DeliveryResult> DeliverOnTurnAsync(Rig rig, InboxMessage envelope)
    {
        var started = new TaskCompletionSource<Task<DeliveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Context.Scheduler.QueueAction(() =>
        {
            try
            {
                Assert.Same(rig.Context, ReceiverTestServices.CurrentGrainContext);
                IMessagingExtension extension = rig.Context.ActivationServices.GetRequiredService<Inbox>();
                started.SetResult(extension.DeliverAsync(envelope, TestContext.Current.CancellationToken).AsTask());
            }
            catch (Exception exception) { started.SetException(exception); }
        });
        return started.Task.Unwrap();
    }

    private static Task OnTurnAsync(IGrainContext context, Action action) =>
        OnTurnAsync(context, () => { action(); return Task.CompletedTask; });

    private static Task OnTurnAsync(IGrainContext context, Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() => { _ = CompleteAsync(); });
        return done.Task;
        async Task CompleteAsync()
        {
            try
            {
                Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
                await action();
                done.SetResult();
            }
            catch (Exception exception) { done.SetException(exception); }
        }
    }

    private static Task WaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private sealed record Rig(IMessagingTestGrain Receiver, IGrainContext Context,
        MessagingTestGrain Grain, IdentityHandler Handler, IJournaledStateManager Manager, Inbox Inbox)
    {
        public IReadOnlyDictionary<MessageId, InboxEntry> Pending => InboxTestAccess.GetPendingMessages(Inbox);
        public IReadOnlyDictionary<MessageId, DateTimeOffset> Processed => InboxTestAccess.GetProcessedMessages(Inbox);
    }

    private sealed class IdentityHandler : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool CanHandle(IInboxHandlerContext context) => true;

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            var services = ReceiverTestServices.CurrentGrainContext!.ActivationServices;
            var effects = services.GetRequiredKeyedService<IDurableDictionary<MessageId, MessageEffect>>("test-effects");
            var application = TestApplicationProtocol.Read(services.GetRequiredService<SerializerSessionPool>(), context.Message);
            var body = Assert.IsType<TestMessage>(application.Body);
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            effects.TryGetValue(context.Message.Envelope.MessageId, out var prior);
            effects[context.Message.Envelope.MessageId] = new MessageEffect(context.Message.Envelope.MessageId,
                (prior?.Count ?? 0) + 1, body.Sequence, body.Value);
            context.Complete();
        }
        public void Dispose() => Release.TrySetResult();
    }
}
