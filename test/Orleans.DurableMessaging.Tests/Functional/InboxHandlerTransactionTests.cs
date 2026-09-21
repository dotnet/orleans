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
public sealed class InboxHandlerTransactionTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task HandlerSuccess_CommitsEffectCompletionDedupeAndStagedOutputAtomically()
    {
        var receiver = NewGrain();
        var sink = NewGrain();
        var logicalId = Guid.NewGuid();
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(logicalId, 7, "atomic", sink.GetGrainId()));

        var result = await DeliverAsync(receiver, envelope.Value);
        var receiverState = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var output = Fixture.GetStagedOutput(receiver);

        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        var effect = Assert.Single(receiverState.Effects);
        Assert.Equal(new DurableEffect(logicalId, 1, 7, "atomic"), effect);
        Assert.Equal(0, receiverState.InboxCount);
        Assert.Equal(1, receiverState.OutboxCount);
        var outgoing = Assert.Single(output);
        Assert.Equal(sink.GetGrainId(), outgoing.ReceiverId);
        Assert.Equal("messages/forwarded", outgoing.RouteKey);
        Assert.True(outgoing.Data.TryGetBody<DurableTestMessage>(out var body));
        Assert.Equal(new DurableTestMessage(logicalId, 7, "atomic"), body);
        Assert.Equal(1, receiverState.ProcessedMessageCount);
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(receiverState.ActivationId, recovered.ActivationId);
        Assert.Equal(effect, Assert.Single(recovered.Effects));
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(outgoing.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).MessageId);

        var duplicate = await DeliverAsync(receiver, envelope.Value);
        Assert.Equal(DeliveryStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        Assert.Single(Fixture.GetStagedOutput(receiver));
    }

    [Fact]
    public async Task HandlerPreparationFailure_DeadLettersWithoutStagingEffectsOrOutput()
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
        Assert.Empty(Fixture.GetStagedOutput(receiver));
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(state.ActivationId, recovered.ActivationId);
        Assert.Empty(recovered.Effects);
        Assert.Empty(Fixture.GetStagedOutput(receiver));
        Assert.Equal(envelope.Value.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
    }

    [Fact]
    public async Task HandlerSelectionFailure_IsDeadLettered()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(80, "selection-failure"),
            "messages/selection-failure");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("Injected handler selection failure", deadLetter.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerSelection_CannotStageOutboundMessages()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(81, "selection-mutation"),
            "messages/selection-mutation");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var state = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Empty(state.Effects);
        Assert.Equal(0, state.OutboxCount);
        Assert.Empty(state.OutboxDeadLetters);
        var deadLetter = Assert.Single(state.InboxDeadLetters);
        Assert.Contains("selection is read-only", deadLetter.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectionMiss_PreservesUnrelatedStagedEffectsForNextWrite()
    {
        var receiver = NewGrain();
        var original = await receiver.GetSnapshotAsync();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journalId);
        var staged = new DurableEffect(Guid.NewGuid(), 1, 82, "application-staging");
        await receiver.StageEffectAsync(staged);
        using var envelope = CreateEnvelope(receiver, NewMessage(83, "route-miss"), "unknown/selection");

        var result = await DeliverAsync(receiver, envelope.Value);
        var selected = await receiver.GetSnapshotAsync();

        Assert.Equal(DeliveryStatus.RouteNotFound, result.Status);
        Assert.Equal(staged, Assert.Single(selected.Effects));
        Assert.Equal(0, selected.InboxCount);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journalId));
        await receiver.RetryWriteStateAsync();
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(original.ActivationId, recovered.ActivationId);
        Assert.Equal(staged, Assert.Single(recovered.Effects));
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task HandlerPreparation_OrdinaryWriteLeavesEffectsLocalUntilApplyAndCompletion()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        using var envelope = CreateEnvelope(receiver, NewMessage(10, "premature-commit") with { CommitDuringHandling = true });
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
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
        var before = await receiver.GetSnapshotAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/completion-failure");
        using var envelope = CreateEnvelope(receiver, NewMessage(78, "completion-failure"), "messages/completion-failure");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var oldContext = Fixture.GetGrainContext(receiver);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        handler.Release();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => oldManager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        _ = await receiver.GetSnapshotAsync();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, completed.ActivationId);
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
        using var envelope = CreateEnvelope(receiver, NewMessage(79, "failure-accounting") with { ThrowDuringPreparation = true }, "messages/failure-accounting");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
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

    [Fact]
    public async Task NullBodyAndContext_DecodeSuccessfullyAndTypedHandlersReceiveNull()
    {
        var receiver = NewGrain();
        using var referenceEnvelope = CreateEnvelope<string?>(
            receiver,
            body: null,
            route: "nullable/reference",
            builder => builder
                .WithContextValue<string?>("null-reference", null)
                .WithContextValue<int?>("null-value", null));

        Assert.True(referenceEnvelope.Value.Data.TryGetBody<string?>(out var referenceBody));
        Assert.Null(referenceBody);
        Assert.True(referenceEnvelope.Value.Data.TryGetContextValue<string?>("null-reference", out var referenceContext));
        Assert.Null(referenceContext);
        Assert.True(referenceEnvelope.Value.Data.TryGetContextValue<int?>("null-value", out var valueContext));
        Assert.Null(valueContext);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, referenceEnvelope.Value)).Status);

        using var valueEnvelope = CreateEnvelope<int?>(receiver, body: null, route: "nullable/value");
        Assert.True(valueEnvelope.Value.Data.TryGetBody<int?>(out var valueBody));
        Assert.Null(valueBody);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, valueEnvelope.Value)).Status);

        var completed = await Fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.NullReferenceMessageCalls == 1
                && snapshot.NullNullableValueMessageCalls == 1);
        Assert.Equal(1, completed.NullReferenceMessageCalls);
        Assert.Equal(1, completed.NullNullableValueMessageCalls);
    }
}
