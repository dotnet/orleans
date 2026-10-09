using Microsoft.Extensions.DependencyInjection;
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
public sealed class InboxIdentityTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task PendingCommand_NewSenderCoalescesAndPreservesOriginalEnvelope()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = HierarchicalKey.Create("tenant", "orders", "1042", "reserve");
        using var original = Create(rig, key, "inventory.reserve.v1", "original");
        using var repeated = original with
        {
            SenderId = GrainId.Create("forwarder", "second"),
            Payload = original.Payload.Slice(0)
        };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        var writes = Writes(rig);
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverOnTurnAsync(rig, repeated)).Status);
        await OnTurnAsync(rig.Context, () =>
        {
            var inbox = rig.Context.ActivationServices.GetRequiredService<IDurableInbox>();
            Assert.True(inbox.TryGetMessage(key, out var stored));
            Assert.Equal(original.SenderId, stored.SenderId);
            Assert.Equal(original.Subject, stored.Subject);
            Assert.Same(original.Payload.First, stored.Payload.First);
            Assert.Single(rig.Pending);
            Assert.Empty(rig.Processed);
        });
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId));
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
        var key = HierarchicalKey.Create("tenant", "orders", "1043", "reserve");
        using var original = Create(rig, key, "inventory.reserve.v1", "original");
        var conflictingSubject = difference switch
        {
            "subject" => "inventory.release.v1",
            "subject-case" => "Inventory.reserve.v1",
            _ => original.Subject
        };
        using var replacement = Create(rig, key, conflictingSubject, "different-parameters",
            GrainId.Create("forwarder", "other"));
        using var conflict = difference == "body" ? replacement.Retain() : original with
        {
            SenderId = replacement.SenderId,
            Subject = conflictingSubject,
            Payload = original.Payload.Slice(0)
        };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        var writes = Writes(rig);
        var job = rig.Grain.GetSnapshotForTest().InboxJob;
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverOnTurnAsync(rig, conflict));
        Assert.Contains(key.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains("different command", failure.Message, StringComparison.Ordinal);
        await OnTurnAsync(rig.Context, () =>
        {
            Assert.Equal(original.SenderId, Assert.Single(rig.Pending).Value.SenderId);
            Assert.Equal(original.Subject, Assert.Single(rig.Pending).Value.Subject);
            Assert.Same(original.Payload.First, Assert.Single(rig.Pending).Value.Payload.First);
            Assert.Empty(rig.Processed);
            Assert.Empty(rig.Grain.GetSnapshotForTest().Effects);
            Assert.Same(job, rig.Grain.GetSnapshotForTest().InboxJob);
            Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        });
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId));
        using var retainedConflict = conflict.Retain();
        Assert.Equal(conflict.Payload.ToArray(), retainedConflict.Payload.ToArray());
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
        var key = HierarchicalKey.Create("tenant", "orders", "1044", "reserve");
        using var original = Create(rig, key, "inventory.reserve.v1", "original");
        using var repeated = Create(rig, key, "completely.changed.subject.v2", "different-body",
            GrainId.Create("forwarder", "new-owner"));
        handler.Release.TrySetResult();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        await Fixture.SnapshotProbe.WaitAsync(rig.Receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        var prior = rig.Context;
        if (restart)
        {
            await rig.Receiver.RequestDeactivationAsync();
            await WaitAsync(prior.Deactivated);
            await rig.Receiver.GetSnapshotAsync();
            Assert.NotSame(prior, Fixture.GetGrainContext(rig.Receiver));
        }
        var writes = Writes(rig);
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, repeated)).Status);
        var completed = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId));
    }

    [Fact]
    public async Task ExactParentChildAndSiblingCommandsHaveIndependentCompletions()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Release.TrySetResult();
        var parent = HierarchicalKey.Create("tenant", "orders", "1045");
        var keys = new[] { parent, parent.CreateChildKey("reserve"), parent.CreateChildKey("release") };
        for (var index = 0; index < keys.Length; index++)
        {
            using var command = Create(rig, keys[index], "inventory.command.v1", $"command-{index}");
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
        var key = HierarchicalKey.Create("tenant", "orders", "1049", "reserve");
        using var one = Create(first, key, "inventory.reserve.v1", "first-receiver");
        using var two = Create(second, key, "inventory.reserve.v1", "second-receiver");
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
        var key = HierarchicalKey.Create("tenant", "orders", "1046", "reserve");
        using var original = Create(rig, key, "inventory.reserve.v1", "original");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        rig.Context.Deactivate(new DeactivationReason(
            DeactivationReasonCode.ApplicationRequested, "Verify pending command identity replay."));
        await WaitAsync(rig.Context.Deactivated);
        using var recoveredHandler = new IdentityHandler();
        using var read = Fixture.Storage.BlockRead(JournalId.FromGrainId(rig.Context.GrainId));
        var activation = rig.Receiver.GetSnapshotAsync();
        await read.WaitUntilEnteredAsync();
        var recoveredContext = Fixture.GetGrainContext(rig.Receiver);
        var recoveredGrain = Assert.IsType<DurableMessagingTestGrain>(recoveredContext.GrainInstance);
        await OnTurnAsync(recoveredContext, () => recoveredGrain.HandlerOverride = recoveredHandler);
        read.Release();
        await WaitAsync(recoveredHandler.Entered.Task);
        var recovered = CreateRig(rig.Receiver, recoveredContext, recoveredHandler);
        Assert.NotSame(rig.Context, recovered.Context);
        using var repeated = original with
        {
            SenderId = GrainId.Create("forwarder", "after-replay"),
            Payload = original.Payload.Slice(0)
        };
        using var conflict = Create(recovered, key, "inventory.reserve.v1", "changed",
            GrainId.Create("forwarder", "after-replay"));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverOnTurnAsync(recovered, repeated)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverOnTurnAsync(recovered, conflict));
        Assert.Equal(original.SenderId, Assert.Single(recovered.Pending).Value.SenderId);
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
        var key = HierarchicalKey.Create("tenant", "orders", "1048", "invalid-command");
        var body = new DurableTestMessage(key, 611, "poison", ThrowDuringPreparation: true);
        using var original = CreateEnvelope(receiver, body);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, original.Value)).Status);
        var deadLetter = Assert.Single((await Fixture.WaitForDeadLetterCountAsync(receiver, 1)).InboxDeadLetters);
        Assert.Equal(key, deadLetter.MessageId);
        var old = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await WaitAsync(old.Deactivated);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.Equal(key, Assert.Single(recovered.InboxDeadLetters).MessageId);
        using var duplicate = original.Value with
        {
            SenderId = GrainId.Create("forwarder", "deadletter"),
            Subject = "another.subject.v1",
            Payload = original.Value.Payload.Slice(0)
        };
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
    [InlineData("key-bytes")]
    [InlineData("key-utf8")]
    [InlineData("key-depth")]
    [InlineData("subject-bytes")]
    [InlineData("subject-utf8")]
    public async Task OversizedMetadata_RejectsBeforeSchedulingPersistenceOrPayloadRetention(string field)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var key = field switch
        {
            "key-bytes" => HierarchicalKey.Create(new string('x', 1025)),
            "key-utf8" => HierarchicalKey.Create(new string('\u00e9', 513)),
            "key-depth" => HierarchicalKey.Create(Enumerable.Repeat("segment", 33).ToArray()),
            _ => HierarchicalKey.Create("tenant", "bounded", "command")
        };
        var subject = field switch
        {
            "subject-bytes" => new string('x', 257),
            "subject-utf8" => new string('\u00e9', 129),
            _ => "inventory.reserve.v1"
        };
        using var envelope = Create(rig, key, subject, "oversized");
        var writes = Writes(rig);
        var bytes = envelope.Payload.ToArray();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => DeliverOnTurnAsync(rig, envelope));
        Assert.Equal(writes, Writes(rig));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, rig.Context.GrainId));
        Assert.Empty(rig.Pending);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Grain.GetSnapshotForTest().Effects);
        Assert.Equal(bytes, envelope.Payload.ToArray());
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        Assert.False(handler.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData("key-bytes")]
    [InlineData("key-utf8")]
    [InlineData("key-depth")]
    [InlineData("subject-bytes")]
    [InlineData("subject-utf8")]
    public async Task MetadataAtExactDefaultLimit_IsAccepted(string field)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Release.TrySetResult();
        var key = field switch
        {
            "key-bytes" => HierarchicalKey.Create(new string('x', 1024)),
            "key-utf8" => HierarchicalKey.Create(new string('\u00e9', 512)),
            "key-depth" => HierarchicalKey.Create(Enumerable.Repeat("segment", 32).ToArray()),
            _ => HierarchicalKey.Create("tenant", "bounded", "command")
        };
        var subject = field switch
        {
            "subject-bytes" => new string('x', 256),
            "subject-utf8" => new string('\u00e9', 128),
            _ => "inventory.reserve.v1"
        };
        using var envelope = Create(rig, key, subject, "maximum");
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
        var key = HierarchicalKey.Create("tenant", "orders", "1047", "reserve");
        using var original = Create(rig, key, "inventory.reserve.v1", "original");
        using var repeated = Create(rig, key, "inventory.reserve.v2", "changed",
            GrainId.Create("forwarder", "new"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, original)).Status);
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        await Fixture.SnapshotProbe.WaitAsync(rig.Receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
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

    private static Rig CreateRig(IDurableMessagingTestGrain receiver, IGrainContext context, IdentityHandler handler) =>
        new(receiver, context, Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance), handler,
            context.ActivationServices.GetRequiredService<IJournaledStateManager>(),
            context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.inbox"),
            context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DateTimeOffset>>("__orleans.durable-messaging.inbox-processed"));

    private DurableEnvelope Create(Rig rig, HierarchicalKey key, string subject, string value, GrainId sender = default) =>
        TestApplicationProtocol.Create(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(),
            sender.IsDefault ? GrainId.Create("producer", "first") : sender, rig.Receiver.GetGrainId(),
            subject, new DurableTestMessage(key, 610, value), key);

    private int Writes(Rig rig) => Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(rig.Context.GrainId));

    private static Task<DeliveryResult> DeliverOnTurnAsync(Rig rig, DurableEnvelope envelope)
    {
        var started = new TaskCompletionSource<Task<DeliveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Context.Scheduler.QueueAction(() =>
        {
            try
            {
                Assert.Same(rig.Context, ReceiverTestServices.CurrentGrainContext);
                var extension = (IDurableInboxExtension)rig.Context.ActivationServices.GetRequiredService(
                    ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
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
    private sealed record Rig(IDurableMessagingTestGrain Receiver, IGrainContext Context,
        DurableMessagingTestGrain Grain, IdentityHandler Handler, IJournaledStateManager Manager,
        IDurableDictionary<HierarchicalKey, DurableEnvelope> Pending,
        IDurableDictionary<HierarchicalKey, DateTimeOffset> Processed);

    private sealed class IdentityHandler : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            var services = ReceiverTestServices.CurrentGrainContext!.ActivationServices;
            var effects = services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEffect>>("test-effects");
            var application = TestApplicationProtocol.Read(services.GetRequiredService<SerializerSessionPool>(), context.Envelope);
            var body = Assert.IsType<DurableTestMessage>(application.Body);
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            effects.TryGetValue(context.Envelope.MessageId, out var prior);
            effects[context.Envelope.MessageId] = new DurableEffect(context.Envelope.MessageId,
                (prior?.Count ?? 0) + 1, body.Sequence, body.Value);
            context.Complete();
        }
        public void Dispose() => Release.TrySetResult();
    }
}
