using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
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
public sealed class InboxHandlerTransactionTests : MessagingBehaviorTestBase
{
    public InboxHandlerTransactionTests() : base(receiverOnly: true)
    {
    }

    [Fact]
    public async Task HandlerSuccess_CommitsEffectCompletionDedupeAndStagedOutputAtomically()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var logicalId = TestApplicationProtocol.NewMessageId();
        var envelope = CreateEnvelope(
            receiver,
            new TestMessage(logicalId, 7, "atomic", sink.GetGrainId()));

        var result = await DeliverAsync(receiver, envelope);
        var receiverState = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var output = Fixture.GetStagedOutput(receiver);

        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        var effect = Assert.Single(receiverState.Effects);
        Assert.Equal(new MessageEffect(logicalId, 1, 7, "atomic"), effect);
        Assert.Equal(0, receiverState.InboxCount);
        Assert.Equal(1, receiverState.OutboxCount);
        var outgoing = Assert.Single(output);
        Assert.Equal(sink.GetGrainId(), outgoing.ReceiverId);
        Assert.Equal("messages/forwarded", TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), outgoing).Route);
        var body = TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), outgoing).Body;
        Assert.Equal(new TestMessage(logicalId, 7, "atomic"), body);
        Assert.Equal(1, receiverState.ProcessedMessageCount);
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(receiverState.ActivationId, recovered.ActivationId);
        Assert.Equal(effect, Assert.Single(recovered.Effects));
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(outgoing.Envelope.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).Envelope.MessageId);

        var duplicate = await DeliverAsync(receiver, envelope);
        Assert.Equal(DeliveryStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        Assert.Single(Fixture.GetStagedOutput(receiver));
    }

    [Fact]
    public async Task HandlerPreparationFailure_DeadLettersWithoutStagingEffectsOrOutput()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var envelope = CreateEnvelope(
            receiver,
            new TestMessage(TestApplicationProtocol.NewMessageId(), 9, "preparation-failure", sink.GetGrainId(), ThrowDuringPreparation: true));

        var accepted = await DeliverAsync(receiver, envelope);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Equal(DeliveryStatus.Accepted, accepted.Status);
        Assert.Empty(state.Effects);
        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.OutboxCount);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Equal(envelope.Envelope.MessageId, deadLetter.MessageId);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Contains("Injected handler preparation failure", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(state.ActivationId, recovered.ActivationId);
        Assert.Empty(recovered.Effects);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
        Assert.Equal(envelope.Envelope.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
    }

    [Fact]
    public async Task HandlerPreparation_OrdinaryWriteLeavesEffectsLocalUntilApplyAndCompletion()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<MessagingTestGrain>(oldContext.GrainInstance);
        var envelope = CreateEnvelope(receiver, NewMessage(10, "premature-commit") with { CommitDuringHandling = true });
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal("premature-commit", Assert.Single(completed.Effects).Value);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.False(oldGrain.DeactivationFailure.Task.IsCompleted);
        Assert.Same(oldContext, Fixture.GetGrainContext(receiver));
    }

    [Fact]
    public async Task HandlerCompletionWriteFailure_FencesOldManagerAndFreshActivationRetries()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/completion-failure");
        var envelope = CreateEnvelope(receiver, NewMessage(78, "completion-failure"), "messages/completion-failure");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<MessagingTestGrain>(oldContext.GrainInstance);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.Release();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => oldManager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != oldGrain.GetSnapshotForTest().ActivationId
                && snapshot.Effects.Sum(static effect => effect.Count) == 1);
        Assert.NotEqual(oldGrain.GetSnapshotForTest().ActivationId, completed.ActivationId);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task PreparationFailureAccountingWriteFailure_RecoversInFreshActivation()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/failure-accounting");
        var envelope = CreateEnvelope(receiver, NewMessage(79, "failure-accounting") with { ThrowDuringPreparation = true }, "messages/failure-accounting");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.Release();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, completed.ActivationId);
        Assert.Equal(1, Assert.Single(completed.InboxDeadLetters).AttemptCount);
        Assert.Empty(completed.Effects);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
    }

}
