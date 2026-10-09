using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
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
public sealed class PublicInboxHandlerTransactionTests : DurableMessagingBehaviorTestBase
{
    public PublicInboxHandlerTransactionTests() : base(receiverOnly: false)
    {
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ArbitraryRawPayload_AcceptanceReplayForwardAndBusinessCompletionRequireNoApplicationCodec(int sends)
    {
        var receiver = Fixture.Client.GetGrain<IRawPayloadTestGrain>(Guid.NewGuid());
        var sink = Fixture.Client.GetGrain<IRawPayloadTestGrain>(Guid.NewGuid());
        await receiver.ConfigureForwardAsync(sink.GetGrainId(), sends);
        _ = await sink.GetSnapshotAsync();
        Assert.True(Fixture.Cluster.TryGetGrainContext(receiver.GetGrainId(), out var original));
        Assert.True(Fixture.Cluster.TryGetGrainContext(sink.GetGrainId(), out var sinkContext));
        var sinkGrain = Assert.IsType<RawPayloadTestGrain>(sinkContext.GrainInstance);
        sinkGrain.ExpectedAcknowledgedEffects = sends;
        var bytes = System.Text.Encoding.UTF8.GetBytes("raw application bytes\0\u03c0/forward").Concat(new byte[] { 0xff, 0x80, 0x01 }).ToArray();
        using var payloadWriter = new Orleans.Serialization.Buffers.ArcBufferWriter();
        payloadWriter.Write(bytes);
        using var envelope = new DurableEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderId = GrainId.Create("raw-external-sender", "1"),
            ReceiverId = receiver.GetGrainId(),
            Payload = payloadWriter.PeekSlice(payloadWriter.Length)
        };
        Assert.Equal(DeliveryStatus.Accepted, (await receiver.AcceptAndDeactivateAsync(envelope)).Status);
        await original.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var accepted = Assert.IsType<RawPayloadSnapshot>(Assert.IsType<RawPayloadTestGrain>(original.GrainInstance).AcceptedSnapshot);
        Assert.Equal(1, accepted.InboxCount);
        Assert.Empty(accepted.Effects);

        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        using var replay = Fixture.Storage.BlockRead(journal);
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), RawPayloadTestGrain.HandlerBarrier);
        var activation = receiver.GetSnapshotAsync();
        await replay.WaitUntilEnteredAsync();
        replay.Release();
        await handler.WaitUntilEnteredAsync();
        Assert.True(Fixture.Cluster.TryGetGrainContext(receiver.GetGrainId(), out var current));
        Assert.NotSame(original, current);
        var grain = Assert.IsType<RawPayloadTestGrain>(current.GrainInstance);
        using var pending = Assert.Single(current.ActivationServices.GetRequiredService<IDurableInbox>().Messages).Retain();
        Assert.Equal(bytes, pending.Payload.ToArray());
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        using var completion = Fixture.Storage.BlockWrite(journal);
        handler.Release();
        await completion.WaitUntilEnteredAsync();
        var staged = grain.GetSnapshotForTest();
        Assert.Equal(0, staged.InboxCount);
        Assert.Equal(sends, staged.OutboxCount);
        Assert.Equal(1, staged.ProcessedCount);
        Assert.Equal(new RawPayloadEffect(envelope.MessageId, Convert.ToBase64String(bytes), 1), Assert.Single(staged.Effects));
        var forwarded = current.ActivationServices.GetRequiredService<IDurableOutbox>().Messages.ToArray();
        Assert.Equal(sends, forwarded.Length);
        Assert.Equal(sends, forwarded.Select(output => output.MessageId).Distinct().Count());
        Assert.Contains(forwarded, output => output.MessageId == envelope.MessageId);
        Assert.All(forwarded, output =>
        {
            Assert.Equal(receiver.GetGrainId(), output.SenderId);
            Assert.Equal(sink.GetGrainId(), output.ReceiverId);
            Assert.Equal(bytes, output.Payload.ToArray());
            Assert.Same(pending.Payload.First, output.Payload.First);
        });
        Assert.False(grain.Acknowledged.IsCompleted);
        Assert.False(sinkGrain.Acknowledged.IsCompleted);
        completion.Release();
        await activation;
        var delivered = await sinkGrain.Acknowledged.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var acknowledged = await grain.Acknowledged.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(Assert.Single(acknowledged.Effects), Assert.Single(delivered.Effects, effect => effect.MessageId == envelope.MessageId));
        Assert.Equal(sends, delivered.Effects.Length);
        Assert.All(delivered.Effects, effect =>
        {
            Assert.Equal(Convert.ToBase64String(bytes), effect.Bytes);
            Assert.Equal(1, effect.Count);
        });
        Assert.Equal(0, acknowledged.OutboxCount);
        Assert.Equal(sends, delivered.ProcessedCount);
        Assert.Empty(current.ActivationServices.GetRequiredService<IDurableMessagingDiagnostics>().InboxDeadLetters);
        Assert.Empty(current.ActivationServices.GetRequiredService<IDurableMessagingDiagnostics>().OutboxDeadLetters);
        await receiver.RequestDeactivationAsync();
        await current.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(acknowledged.ActivationId, recovered.ActivationId);
        Assert.Equal(acknowledged.Effects, recovered.Effects);
        Assert.Equal(1, recovered.ProcessedCount);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        var sinkState = await sink.GetSnapshotAsync();
        Assert.Equal(sends, sinkState.Effects.Length);
        Assert.All(sinkState.Effects, effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task CaptureProbeSnapshot_RemainsStableAcrossLaterWrites()
    {
        var receiver = NewGrain();
        var first = new DurableEffect(Guid.NewGuid(), 1, 1, "first");
        var second = new DurableEffect(Guid.NewGuid(), 1, 2, "second");
        await receiver.StageEffectAsync(first);
        await receiver.RetryWriteStateAsync();
        var grain = Assert.IsType<DurableMessagingTestGrain>(Fixture.GetGrainContext(receiver).GrainInstance);
        var captures = grain.Captures;
        var originalCount = captures.Count;
        using var enumerator = captures.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var observed = new List<DurableEndpointSnapshot> { enumerator.Current };

        await receiver.StageEffectAsync(second);
        await receiver.RetryWriteStateAsync();

        while (enumerator.MoveNext())
        {
            observed.Add(enumerator.Current);
        }

        Assert.Equal(originalCount, captures.Count);
        Assert.Equal(originalCount, observed.Count);
        Assert.Equal(captures, observed);
        Assert.DoesNotContain(captures, capture => capture.Effects.Contains(second));
        Assert.Contains(grain.Captures, capture => capture.Effects.Contains(second));
    }

    [Fact]
    public async Task HandlerSuccess_CommitsEffectCompletionDedupeAndOutgoingAtomically()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var logicalId = Guid.NewGuid();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(logicalId, 7, "atomic", sink.GetGrainId()));

        var result = await DeliverAsync(receiver, envelope.Value);
        var receiverState = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var sinkState = await Fixture.WaitForEffectCountAsync(sink, 1);
        receiverState = await Fixture.WaitForOutboxCountAsync(receiver, 0);

        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        var effect = Assert.Single(receiverState.Effects);
        Assert.Equal(new DurableEffect(logicalId, 1, 7, "atomic"), effect);
        Assert.Equal(0, receiverState.InboxCount);
        Assert.Equal(0, receiverState.OutboxCount);
        Assert.Equal(effect, Assert.Single(sinkState.Effects));

        var duplicate = await DeliverAsync(receiver, envelope.Value);
        Assert.Equal(DeliveryStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        Assert.Equal(1, Assert.Single((await sink.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task HandlerPreparationFailure_DeadLettersWithoutEffectsOrSinkDelivery()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(Guid.NewGuid(), 9, "preparation-failure", sink.GetGrainId(), ThrowDuringPreparation: true));

        var accepted = await DeliverAsync(receiver, envelope.Value);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Equal(DeliveryStatus.Accepted, accepted.Status);
        Assert.Empty(state.Effects);
        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.OutboxCount);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Contains("Injected handler preparation failure", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Empty((await sink.GetSnapshotAsync()).Effects);
    }
    [Fact]
    public async Task HandlerApplicationPreparation_AllowsIndependentWritesAndPreservesCancelledWriteWait()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/admitted-outgoing");
        using var envelope = CreateEnvelope(receiver,
            new DurableTestMessage(Guid.NewGuid(), 91, "admitted", sink.GetGrainId()), "messages/admitted-outgoing");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var localPreparation = grain.GetSnapshotForTest();
        Assert.Empty(localPreparation.Effects);
        Assert.Equal(1, localPreparation.InboxCount);
        Assert.Equal(0, localPreparation.ProcessedMessageCount);
        Assert.Equal(0, localPreparation.OutboxCount);
        using var applicationPreparation = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/admitted-outgoing/application-preparation");
        using var scheduling = Fixture.JobManagerProbe.BlockNext("orleans.messaging.outbox-flush");
        handler.Release();
        await applicationPreparation.WaitUntilEnteredAsync();
        var preparing = grain.GetSnapshotForTest();
        Assert.Empty(preparing.Effects);
        Assert.Equal(1, preparing.InboxCount);
        Assert.Equal(0, preparing.ProcessedMessageCount);
        Assert.Equal(0, preparing.OutboxCount);
        Assert.Null(preparing.OutboxJob);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(grain.Captures, snapshot => snapshot.Effects.Count > 0 || snapshot.OutboxCount > 0);

        applicationPreparation.Release();
        await scheduling.WaitUntilEnteredAsync();
        var staged = grain.GetSnapshotForTest();
        Assert.Single(staged.Effects);
        Assert.Equal(0, staged.InboxCount);
        Assert.Equal(1, staged.ProcessedMessageCount);
        Assert.Equal(1, staged.OutboxCount);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        using var cancellation = new CancellationTokenSource();
        var storage = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        Task completedWait;
        try
        {
            scheduling.Continue();
            await storage.WaitUntilEnteredAsync();
            var captured = grain.GetSnapshotForTest();
            Assert.Equal(0, captured.InboxCount);
            Assert.Equal(1, captured.ProcessedMessageCount);
            Assert.Equal(1, captured.OutboxCount);
            Assert.Single(captured.Effects);
            var owner = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs("orleans.messaging.outbox-flush", receiver.GetGrainId()));
            Assert.Equal(owner.Id, captured.OutboxJob?.Id);
            Assert.Equal(owner.ShardId, captured.OutboxJob?.ShardId);
            Assert.Equal(owner.Metadata!["orleans.messaging.ownership-id"], captured.OutboxJobId);
            var canceledWait = manager.WriteStateAsync(cancellation.Token).AsTask();
            completedWait = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.False(canceledWait.IsCompleted);
            Assert.False(completedWait.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
            Assert.False(completedWait.IsCompleted);
            Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
            Assert.Empty((await sink.GetSnapshotAsync()).Effects);
        }
        finally
        {
            storage.Release();
        }
        await completedWait;
        var delivered = await Fixture.WaitForEffectCountAsync(sink, 1);
        var completed = await Fixture.WaitForOutboxCountAsync(receiver, 0);
        Assert.Equal(Assert.Single(completed.Effects), Assert.Single(delivered.Effects));
        Assert.DoesNotContain(grain.Captures, snapshot => snapshot.Effects.Count > 0 && snapshot.ProcessedMessageCount == 0);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerCompletionPersistenceFailure_FreshActivationConvergesEffectDedupeAndSink(bool storageCommitted)
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/completion-outcome");
        var message = new DurableTestMessage(Guid.NewGuid(), 92, "completion-outcome", sink.GetGrainId());
        using var envelope = CreateEnvelope(receiver, message, "messages/completion-outcome");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        if (storageCommitted)
        {
            Fixture.Storage.FailAfterWrite(journal);
        }
        else
        {
            Fixture.Storage.FailWrite(journal);
        }
        handler.Release();
        Assert.IsType<IOException>(await oldGrain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId && snapshot.Effects.Count == 1);
        var delivered = await Fixture.WaitForEffectCountAsync(sink, 1);
        completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId && snapshot.Effects.Count == 1 && snapshot.OutboxCount == 0);

        Assert.NotEqual(before.ActivationId, completed.ActivationId);
        Assert.Equal(new DurableEffect(message.LogicalId, 1, message.Sequence, message.Value), Assert.Single(completed.Effects));
        Assert.Equal(Assert.Single(completed.Effects), Assert.Single(delivered.Effects));
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Empty(completed.OutboxDeadLetters);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await sink.GetSnapshotAsync()).Effects).Count);
    }

}
