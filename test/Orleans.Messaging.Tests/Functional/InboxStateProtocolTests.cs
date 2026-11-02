using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxStateProtocolTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task RawWriteDuringLocalPreparation_CapturesNoHandlerEffects()
    {
        var receiver = NewGrain();
        const string route = "messages/local-preparation";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(201, "local"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        await OnTurnAsync(context, () => context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "raw-write");
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.False(grain.ApplyAttempted.Task.IsCompleted);
        var beforeApply = grain.GetSnapshotForTest();
        Assert.Empty(beforeApply.Effects);
        Assert.Equal(1, beforeApply.InboxCount);
        Assert.Equal(0, beforeApply.ProcessedMessageCount);
        var captured = grain.Captures[^1];
        Assert.Empty(captured.Effects);
        Assert.Equal(1, captured.InboxCount);
        Assert.Equal(0, captured.ProcessedMessageCount);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        await receiver.RequestDeactivationAsync();
        var replayed = await receiver.GetSnapshotAsync();
        Assert.Equal(completed.Effects, replayed.Effects);
        Assert.Equal(1, replayed.ProcessedMessageCount);
    }

    [Fact]
    public async Task HandlerFailureBeforeComplete_AccountsPreparationOutcomeWithoutMutationInference()
    {
        var receiver = NewGrain();
        const string route = "messages/no-earlier-write";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(203, "no-earlier-write"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var failure = new IOException("Local preparation failed before Complete.");
        await OnTurnAsync(context, () =>
        {
            Assert.Equal(0, manager.PendingWriteByteCount);
            grain.NextApplyFailure = failure;
        });

        handler.Release();
        var completed = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Contains(failure.Message, Assert.Single(completed.InboxDeadLetters).Reason, StringComparison.Ordinal);
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Empty(completed.Effects);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Empty(recovered.Effects);
        Assert.Equal(completed.InboxDeadLetters, recovered.InboxDeadLetters);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(0, recovered.InboxCount);
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }
}
