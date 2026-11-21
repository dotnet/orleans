using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Durable.Messaging.Tests.Functional;

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

        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(result);
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
        DeliveryResultAssert.IsCase<DeliveryResult.Duplicate>(duplicate);
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

        // Each failed attempt fails its activation without staging anything. With this fixture's zero
        // InboxRetryTimeout, the first recovery starts the retry window and the second dead-letters the message.
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(accepted);
        Assert.Empty(state.Effects);
        Assert.Equal(0, state.InboxCount);
        Assert.Equal(0, state.OutboxCount);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Equal(envelope.Envelope.MessageId, deadLetter.MessageId);
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Equal(InboxTestAccess.InterruptedAttemptReason, deadLetter.Reason);
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
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
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
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
        await handler.WaitUntilEnteredAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<MessagingTestGrain>(oldContext.GrainInstance);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var oldActivationId = oldGrain.GetSnapshotForTest().ActivationId;
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.Release();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => oldManager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != oldActivationId
                && snapshot.Effects.Sum(static effect => effect.Count) == 1);
        Assert.NotEqual(oldActivationId, completed.ActivationId);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(0, completed.InboxCount);
    }

    [Fact]
    public async Task InterruptedAttemptChargeWriteFailure_RecoversInFreshActivation()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        const string route = "messages/charge-write-failure";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(79, "charge-write-failure") with { ThrowDuringPreparation = true }, route);
        DeliveryResultAssert.IsCase<DeliveryResult.Accepted>(await DeliverAsync(receiver, envelope));
        await handler.WaitUntilEnteredAsync();
        var oldContext = Fixture.GetGrainContext(receiver);

        // The failed attempt writes nothing, so the next write is the charge which recovery stages for it.
        using var chargeWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.Release();
        await chargeWrite.WaitUntilEnteredAsync();
        var charging = Assert.IsAssignableFrom<IGrainContext>(chargeWrite.EntryContext);
        Assert.NotSame(oldContext, charging);
        chargeWrite.Fail();
        await charging.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var completed = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, completed.ActivationId);
        // The uncommitted charge doesn't count: a later recovery charges the first attempt again, starting the retry
        // window, and the recovery after the rerun's failure dead-letters the message at its second attempt.
        Assert.Equal(2, Assert.Single(completed.InboxDeadLetters).AttemptCount);
        Assert.Empty(completed.Effects);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
    }

}
